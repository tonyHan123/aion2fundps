using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text;
using Aion2FunDps.Core;
using Aion2FunDps.Core.Models;
using Aion2FunDps.Protocol.Handlers;

namespace Aion2FunDps.Protocol;

/// <summary>
/// Routes GamePackets to opcode-specific handlers, emits typed IGameEvents.
/// Handles LZ4-compressed packets recursively (decompress → re-frame inner packets).
/// </summary>
public sealed class PacketDispatcher
{
    private readonly Lz4Decompressor _lz4 = new();

    private long _knownCount;
    private long _unknownCount;
    private long _malformedCount;
    private long _selfNickSeen;
    private long _selfNickParsed;
    private long _otherNickSeen;
    private long _otherNickParsed;
    private int _currentLobbyRoomId;

    public Lz4Decompressor Lz4 => _lz4;
    public long KnownCount => Volatile.Read(ref _knownCount);
    public long UnknownCount => Volatile.Read(ref _unknownCount);
    public long MalformedCount => Volatile.Read(ref _malformedCount);

    /// <summary>
    /// Per-opcode (op0|op1) frequency for unhandled packets. Diagnostic only —
    /// surfaces the most common "unknown" packets so we can prioritise which to
    /// implement next (e.g. a new mechanic in Patch X spawns a flood of one new opcode).
    /// </summary>
    private readonly ConcurrentDictionary<ushort, long> _unknownByOpcode = new();
    public IReadOnlyDictionary<ushort, long> UnknownByOpcode => _unknownByOpcode;
    public long SelfNickSeen => Volatile.Read(ref _selfNickSeen);
    public long SelfNickParsed => Volatile.Read(ref _selfNickParsed);
    public long OtherNickSeen => Volatile.Read(ref _otherNickSeen);
    public long OtherNickParsed => Volatile.Read(ref _otherNickParsed);

    // Diagnostic: log raw bytes of failed SELF_NICK / OTHER_NICK to file for offline analysis
    public string? DiagnosticLogPath { get; set; }
    private readonly object _logLock = new();

    // One-shot sniffer for unknown opcodes — captures first N raw packets per opcode
    // to <path>. Used to reverse-engineer new opcode layouts (e.g. 0x00 0x36 mob spawn)
    // without polluting normal output.
    public string? UnknownSnifferLogPath { get; set; }
    public int UnknownSnifferPerOpcodeCap { get; set; } = 2000;  // bumped from 200 — party assembly
                                                                  // packets are rare (1~2 per dungeon
                                                                  // entry) and got drowned out by
                                                                  // common-opcode floods at low cap.
    private readonly ConcurrentDictionary<ushort, int> _unknownSampledCount = new();

    // Dedicated logger for 0x01 0x91 (ENCOUNTER_ANNOUNCE) raw bytes. Once that opcode
    // moved into the known-handler path, the unknown sniffer stopped capturing it,
    // and we lost visibility for offline analysis. This logger restores that.
    public string? EncounterAnnounceLogPath { get; set; }

    // Dedicated logger for 0x20 0x36 profile/detail packets. These packets contain
    // a zlib-compressed body and appear right before SELF_NICK for the inspected
    // character, so they are good combat-power suspects.
    public string? ProfileDebugLogPath { get; set; }
    public int ProfileDebugRawByteCap { get; set; } = 12_000;

    // Dedicated logger for 0x40 0x36 SUMMON_SPAWN — to check if boss entities
    // (in 초월 dungeons where 0x01 0x91 doesn't carry boss mob_code) come through
    // this opcode. If yes, our existing handler should already be registering them;
    // if not, mob_code must be in yet another packet.
    public string? SummonSpawnLogPath { get; set; }
    /// <summary>Diagnostic log for op=0297 (party-assembly broadcast) — every
    /// packet's raw bytes + parsed member count + nicknames. Used to verify
    /// the handler picks up all members when the user reports missing rows.</summary>
    public string? PartyAssemblyLogPath { get; set; }

    /// <summary>Diagnostic log for every PartyMemberStatusHandler match —
    /// opcode + entityId + nickname + first 64 bytes. Used to identify which
    /// opcodes are leaking through (e.g. friend-online status broadcasts that
    /// mistakenly get treated as live party-member pings, surfacing random
    /// names on the meter when the user is in town/idle).</summary>
    public string? LiveStatusLogPath { get; set; }

    /// <summary>Diagnostic log for op=6ae2 (zone-load bulk) — every parsed
    /// nickname + raw header. Friend list might ride the same packet at
    /// dungeon entry and get scanned as party members; this log reveals
    /// which names the byte-scan picked up and lets us tighten the parse.</summary>
    public string? BulkInfoLogPath { get; set; }

    /// <summary>Catch-all diagnostic for the full party-related opcode family
    /// (every 0x?? 0x97 and 0x?? 0xe2). Logs BEFORE any handler-specific
    /// branching so it captures even opcodes that get rejected by the
    /// PartyMemberStatusHandler isHandled gate. Used to spot new opcodes
    /// the server emits in scenarios we haven't seen yet — e.g.,
    /// pre-formed-party room joiners (사용자 보고 2026-05-02 23:30+: 김두한,
    /// 노랑나나, 별내역 입장이 어떤 로그에도 안 잡힘 → 서버가 다른 opcode로
    /// 보내는 듯).</summary>
    public string? PartyFamilyLogPath { get; set; }

    private static string ToHex(ReadOnlySpan<byte> bytes) =>
        string.Join(" ", bytes.ToArray().Select(b => b.ToString("x2")));

    private void LogPacket(string opcodeName, ReadOnlySpan<byte> body, bool parsed)
    {
        if (DiagnosticLogPath == null) return;
        try
        {
            // 2400-byte cap — captures FULL nick packets (~2254 bytes) so we can RE
            // fields past the first 256-byte slice (e.g., combat-power, ratings).
            // Bump back down once these fields are localised — log file growth is
            // ~7KB per packet at this cap.
            var hex = string.Join(" ", body[..Math.Min(6000, body.Length)].ToArray().Select(b => b.ToString("x2")));
            lock (_logLock)
            {
                System.IO.File.AppendAllText(DiagnosticLogPath,
                    $"{DateTime.Now:HH:mm:ss.fff} {opcodeName} parsed={parsed} len={body.Length}: {hex}\n");
            }

            if (opcodeName == "SELF_NICK")
            {
                LogPowerHuntPacket(opcodeName, body, always: true);
            }
            else if (opcodeName == "OTHER_NICK")
            {
                LogPowerHuntPacket(opcodeName, body, always: false);
            }
        }
        catch { }
    }

    private void LogPartyRoomList(
        ReadOnlySpan<byte> body,
        IReadOnlyList<PartyAssemblyHandler.RoomBlock> rooms,
        PartyAssemblyHandler.RoomBlock? matched)
    {
        if (PartyAssemblyLogPath == null) return;

        try
        {
            var hex = string.Join(" ", body[..Math.Min(512, body.Length)].ToArray().Select(b => b.ToString("x2")));
            string roomSummary = string.Join(";",
                rooms.Take(16).Select(r => $"{r.GroupId}:{r.Members.Count}:{string.Join(",", r.Members.Select(m => m.Nickname))}"));
            string matchedText = matched.HasValue
                ? $"{matched.Value.GroupId}:{matched.Value.Members.Count}"
                : "none";

            lock (_logLock)
            {
                System.IO.File.AppendAllText(PartyAssemblyLogPath,
                    $"{DateTime.Now:HH:mm:ss.fff} OP0197 current={_currentLobbyRoomId} matched={matchedText} rooms={rooms.Count} [{roomSummary}] len={body.Length}: {hex}\n");
            }
        }
        catch { }
    }

    private void LogProfilePacket(ReadOnlySpan<byte> body)
    {
        if (ProfileDebugLogPath == null) return;

        try
        {
            var raw = body[..Math.Min(ProfileDebugRawByteCap, body.Length)].ToArray();
            int zlibOffset = IndexOf(raw, stackalloc byte[] { 0x78, 0x9c });

            var sb = new StringBuilder();
            sb.AppendLine($"{DateTime.Now:HH:mm:ss.fff} PROFILE_2036 len={body.Length} logged={raw.Length} zlibOffset={zlibOffset}");
            if (body.Length >= 8)
            {
                int idAt4 = body[4] | (body[5] << 8) | (body[6] << 16) | (body[7] << 24);
                sb.AppendLine($"  id@4={idAt4}");
            }

            sb.AppendLine("  raw-candidates:");
            AppendCandidateSummary(sb, raw);

            if (zlibOffset >= 0)
            {
                try
                {
                    using var input = new MemoryStream(raw, zlibOffset, raw.Length - zlibOffset);
                    using var zlib = new ZLibStream(input, CompressionMode.Decompress);
                    using var output = new MemoryStream();
                    zlib.CopyTo(output);
                    var decompressed = output.ToArray();

                    sb.AppendLine($"  zlib-decompressed={decompressed.Length}");
                    sb.AppendLine("  zlib-candidates:");
                    AppendCandidateSummary(sb, decompressed);
                    sb.AppendLine($"  zlib-head: {ToHex(decompressed.AsSpan(0, Math.Min(256, decompressed.Length)))}");
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"  zlib-error={ex.GetType().Name}: {ex.Message}");
                }
            }

            sb.AppendLine($"  raw: {ToHex(raw)}");

            lock (_logLock)
            {
                System.IO.File.AppendAllText(ProfileDebugLogPath, sb.ToString());
            }
        }
        catch { }
    }

    private static int IndexOf(ReadOnlySpan<byte> source, ReadOnlySpan<byte> pattern)
    {
        if (pattern.Length == 0) return 0;
        for (int i = 0; i <= source.Length - pattern.Length; i++)
        {
            if (source.Slice(i, pattern.Length).SequenceEqual(pattern))
                return i;
        }
        return -1;
    }

    private static void AppendCandidateSummary(StringBuilder sb, byte[] bytes)
    {
        int written = 0;
        AppendIntegerCandidates(sb, bytes, 450_000, 456_000, ref written);
        AppendIntegerCandidates(sb, bytes, 350_000, 900_000, ref written);
        AppendFloatCandidates(sb, bytes, ref written);
        if (written == 0) sb.AppendLine("    (none)");
    }

    private static void AppendIntegerCandidates(StringBuilder sb, byte[] bytes, uint min, uint max, ref int written)
    {
        for (int i = 0; i <= bytes.Length - 4; i++)
        {
            uint value = BitConverter.ToUInt32(bytes, i);
            if (value < min || value > max) continue;
            if (written++ >= 80) return;

            int from = Math.Max(0, i - 12);
            int len = Math.Min(bytes.Length - from, 28);
            sb.AppendLine($"    u32@{i}={value} ctx={ToHex(bytes.AsSpan(from, len))}");
        }
    }

    private void LogPowerHuntPacket(string label, ReadOnlySpan<byte> body, bool always)
    {
        if (ProfileDebugLogPath == null) return;

        try
        {
            var raw = body[..Math.Min(ProfileDebugRawByteCap, body.Length)].ToArray();
            bool hasFocusedCandidate = HasIntegerCandidate(raw, 430_000, 480_000)
                                    || HasFloatCandidate(raw, 430f, 480f);
            if (!always && !hasFocusedCandidate) return;

            var sb = new StringBuilder();
            sb.AppendLine($"{DateTime.Now:HH:mm:ss.fff} POWER_HUNT {label} len={body.Length} logged={raw.Length} focused={hasFocusedCandidate}");
            AppendCandidateSummary(sb, raw);

            lock (_logLock)
            {
                System.IO.File.AppendAllText(ProfileDebugLogPath, sb.ToString());
            }
        }
        catch { }
    }

    private static bool HasIntegerCandidate(byte[] bytes, uint min, uint max)
    {
        for (int i = 0; i <= bytes.Length - 4; i++)
        {
            uint value = BitConverter.ToUInt32(bytes, i);
            if (value >= min && value <= max) return true;
        }
        return false;
    }

    private static bool HasFloatCandidate(byte[] bytes, float min, float max)
    {
        for (int i = 0; i <= bytes.Length - 4; i++)
        {
            float value = BitConverter.ToSingle(bytes, i);
            if (!float.IsNaN(value) && value >= min && value <= max) return true;
        }
        return false;
    }

    private static void AppendFloatCandidates(StringBuilder sb, byte[] bytes, ref int written)
    {
        for (int i = 0; i <= bytes.Length - 4; i++)
        {
            float value = BitConverter.ToSingle(bytes, i);
            if (float.IsNaN(value) || value < 350f || value > 900f) continue;
            if (written++ >= 100) return;

            int from = Math.Max(0, i - 12);
            int len = Math.Min(bytes.Length - from, 28);
            sb.AppendLine($"    f32@{i}={value:F3} ctx={ToHex(bytes.AsSpan(from, len))}");
        }
    }

    public void Dispatch(in GamePacket gp, Action<IGameEvent> emit)
    {
        var data = gp.Data;
        if (CompressionDetector.IsCompressed(data, out int compOffset))
        {
            DispatchCompressed(data[compOffset..], gp.SourceIpv4, gp.TimestampTicks, emit);
            return;
        }

        DispatchOpcode(data, gp.SourceIpv4, gp.TimestampTicks, emit);
    }

    private void DispatchCompressed(ReadOnlySpan<byte> compressedSection, uint sourceIpv4, long ticks, Action<IGameEvent> emit)
    {
        // Aion 2 layout after 0xff 0xff marker:
        //   [originLength: uint32 LE, 4 bytes] [LZ4 compressed data]
        if (compressedSection.Length < 5) return;

        int originLength = compressedSection[0]
                         | (compressedSection[1] << 8)
                         | (compressedSection[2] << 16)
                         | (compressedSection[3] << 24);

        // No .ToArray() — Lz4Decompressor.TryDecompress takes a ReadOnlySpan
        // directly, so the compressed payload is read straight from the
        // capture buffer with zero allocation. Compressed packets fire at
        // every game tick during combat; the prior copy was the largest
        // remaining per-packet alloc on the hot path.
        _lz4.TryDecompress(compressedSection[4..], originLength, decompressed =>
        {
            var span = decompressed.Span;
            int offset = 0;
            while (offset < span.Length)
            {
                if (!FrameAssembler.TryReadVarInt(span[offset..], out int v, out int vBytes))
                    break;

                // varint == 0 means skip 1 byte (per TK StreamProcessor)
                if (v == 0)
                {
                    offset += 1;
                    continue;
                }

                int realLen = v + vBytes - 4;
                if (realLen <= vBytes || span.Length - offset < realLen)
                    break;

                var innerBody = span.Slice(offset + vBytes, realLen - vBytes);
                DispatchOpcode(innerBody, sourceIpv4, ticks, emit);
                offset += realLen;
            }
        });
    }

    private void DispatchOpcode(ReadOnlySpan<byte> body, uint sourceIpv4, long ticks, Action<IGameEvent> emit)
    {
        if (body.Length < 2)
        {
            Interlocked.Increment(ref _malformedCount);
            return;
        }

        byte op0 = body[0];
        byte op1 = body[1];

        // Diagnostic: catch-all log for the entire party-related opcode family
        // (0x?? 0x97 and 0x?? 0xe2). Fires BEFORE any handler-specific branch
        // so it captures even opcodes that the PartyMemberStatusHandler
        // isHandled gate rejects. Use case: pre-formed-party room joiner
        // packets that don't show up in any other log — see this log to
        // identify the actual opcode the server emits.
        if (PartyFamilyLogPath != null && (op1 == 0x97 || op1 == 0xe2))
        {
            try
            {
                var hex = string.Join(" ", body[..Math.Min(64, body.Length)].ToArray().Select(b => b.ToString("x2")));
                string srcIp = $"{(sourceIpv4 >> 24) & 0xFF}.{(sourceIpv4 >> 16) & 0xFF}.{(sourceIpv4 >> 8) & 0xFF}.{sourceIpv4 & 0xFF}";
                lock (_logLock)
                {
                    System.IO.File.AppendAllText(PartyFamilyLogPath,
                        $"{DateTime.Now:HH:mm:ss.fff} op={op0:x2}{op1:x2} src={srcIp} len={body.Length}: {hex}\n");
                }
            }
            catch { }
        }

        // 0x04 0x38 = DAMAGE
        if (op0 == 0x04 && op1 == 0x38)
        {
            if (DamageHandler.TryParse(body, ticks, sourceIpv4, isDot: false, out var dmg))
            {
                emit(dmg);
                Interlocked.Increment(ref _knownCount);
            }
            else
            {
                Interlocked.Increment(ref _malformedCount);
            }
            return;
        }

        // 0x00 0x92 = COMBAT_POWER (dedicated CP broadcast).
        // RE'd from A2Viewer May 2026 — fires periodically while in dungeon
        // carrying nickname+CP pairs. This is the channel that keeps party
        // CP fresh after the matchmaking-room op=0297 broadcasts stop firing.
        if (op0 == 0x00 && op1 == 0x92)
        {
            if (CombatPowerHandler.TryParse(body, ticks, sourceIpv4, out var cpu))
            {
                emit(cpu);
                Interlocked.Increment(ref _knownCount);
            }
            else
            {
                Interlocked.Increment(ref _malformedCount);
            }
            return;
        }

        // 0x00 0x8d = MOB_HP
        if (op0 == 0x00 && op1 == 0x8d)
        {
            if (MobHpHandler.TryParse(body, ticks, sourceIpv4, out var hp))
            {
                emit(hp);
                Interlocked.Increment(ref _knownCount);
            }
            else
            {
                Interlocked.Increment(ref _malformedCount);
            }
            return;
        }

        // 0x33 0x36 = SELF_NICK
        if (op0 == 0x33 && op1 == 0x36)
        {
            Interlocked.Increment(ref _selfNickSeen);
            bool ok = SelfNicknameHandler.TryParse(body, ticks, sourceIpv4, out var nick);
            LogPacket("SELF_NICK", body, ok);
            if (ok)
            {
                Interlocked.Increment(ref _selfNickParsed);
                emit(nick);
                Interlocked.Increment(ref _knownCount);
            }
            else
            {
                Interlocked.Increment(ref _malformedCount);
            }
            return;
        }

        // 0x21 0x8d = COMBAT_BOUNDARY
        if (op0 == 0x21 && op1 == 0x8d)
        {
            if (CombatBoundaryHandler.TryParse(body, ticks, sourceIpv4, out var cb))
            {
                emit(cb);
                Interlocked.Increment(ref _knownCount);
            }
            else
            {
                Interlocked.Increment(ref _malformedCount);
            }
            return;
        }

        // 0x05 0x38 = DOT (damage over time)
        if (op0 == 0x05 && op1 == 0x38)
        {
            if (DotHandler.TryParse(body, ticks, sourceIpv4, out var dot))
            {
                emit(dot);
                Interlocked.Increment(ref _knownCount);
            }
            else
            {
                Interlocked.Increment(ref _malformedCount);
            }
            return;
        }

        // 0x44 0x36 = OTHER_NICK
        if (op0 == 0x44 && op1 == 0x36)
        {
            Interlocked.Increment(ref _otherNickSeen);
            bool ok = OtherNicknameHandler.TryParse(body, ticks, sourceIpv4, out var nick);
            LogPacket("OTHER_NICK", body, ok);
            if (ok)
            {
                Interlocked.Increment(ref _otherNickParsed);
                emit(nick);
                Interlocked.Increment(ref _knownCount);
            }
            else
            {
                Interlocked.Increment(ref _malformedCount);
            }
            return;
        }

        // 0x01 0x91 = ENCOUNTER_ANNOUNCE (boss intro banner — used to link entityId → mob_code)
        if (op0 == 0x01 && op1 == 0x91)
        {
            // Diagnostic: log every announce packet's raw bytes + parsed mobCode so
            // we can spot layout drift (e.g. an instance type whose offset 13 isn't mobCode).
            if (EncounterAnnounceLogPath != null)
            {
                try
                {
                    var hex = string.Join(" ", body[..Math.Min(64, body.Length)].ToArray().Select(b => b.ToString("x2")));
                    int candidateMobCode = body.Length >= 17
                        ? body[13] | (body[14] << 8) | (body[15] << 16) | (body[16] << 24)
                        : -1;
                    lock (_logLock)
                    {
                        System.IO.File.AppendAllText(EncounterAnnounceLogPath,
                            $"{DateTime.Now:HH:mm:ss.fff} len={body.Length} parsed_mobCode={candidateMobCode}: {hex}\n");
                    }
                }
                catch { }
            }

            if (EncounterAnnounceHandler.TryParse(body, ticks, sourceIpv4, out var enc))
            {
                emit(enc);
                Interlocked.Increment(ref _knownCount);
            }
            else
            {
                Interlocked.Increment(ref _malformedCount);
            }

            return;
        }

        // 0x01 0x97 — multi-room lobby broadcast (different opcode from 0x01 0x91!).
        // Carries the user's matchmaking room AND adjacent rooms shown in the
        // lobby browser, all bundled into one large packet (~500-1500 bytes).
        // Earlier code mistakenly treated this as 0x91 (encounter announce) so
        // it fell through to the unknown sniffer and we never extracted member
        // info. Self-presence gate: only emit PartyRosterUpdate when the user's
        // own user_id appears in the parsed members — otherwise we'd be applying
        // some random adjacent room's roster as our party (the bug 짭호 saw at
        // the start of expedition entry, before op=0297 sync fired).
        if (op0 == 0x01 && op1 == 0x97)
        {
            byte[] copy0197 = body.ToArray();
            var rooms = PartyAssemblyHandler.ParseRoomBlocks(copy0197, startPos: 2, ticks, sourceIpv4);
            int? selfId = SelfNicknameHandler.LastSelfUserId;
            string? selfNickname = SelfNicknameHandler.LastSelfNickname;

            // Best-block selection. ParseRoomBlocks can return multiple blocks
            // with the same roomId (heuristic header detection occasionally
            // splits a single room into two when a member-record byte sequence
            // looks like a header). We prefer self-bearing blocks unconditionally
            // — they're the only ones safe to use for weak-snapshot removal —
            // and break ties by member count (more complete parse = more trust).
            // A roomId match without self stays as a fallback (add-only).
            PartyAssemblyHandler.RoomBlock? bestSelfBlock = null;
            PartyAssemblyHandler.RoomBlock? bestRoomIdBlock = null;
            foreach (var room in rooms)
            {
                bool hasSelfId = selfId.HasValue && room.Members.Any(m => m.UserId == selfId.Value);
                bool hasSelfNickname = !string.IsNullOrWhiteSpace(selfNickname)
                    && room.Members.Any(m => string.Equals(m.Nickname, selfNickname, StringComparison.Ordinal));
                bool hasSelf = hasSelfId || hasSelfNickname;
                bool isCurrentRoom = _currentLobbyRoomId != 0 && room.GroupId == _currentLobbyRoomId;

                if (hasSelf)
                {
                    if (bestSelfBlock is null || room.Members.Count > bestSelfBlock.Value.Members.Count)
                        bestSelfBlock = room;
                }
                else if (isCurrentRoom)
                {
                    if (bestRoomIdBlock is null || room.Members.Count > bestRoomIdBlock.Value.Members.Count)
                        bestRoomIdBlock = room;
                }
            }

            var matched = bestSelfBlock ?? bestRoomIdBlock;
            bool containsSelf = bestSelfBlock.HasValue;

            if (matched is { } currentRoom)
            {
                _currentLobbyRoomId = currentRoom.GroupId;
                LogPartyRoomList(copy0197, rooms, currentRoom);
                if (currentRoom.Members.Count == 0)
                {
                    // op=0197 is a noisy multi-room list. An empty parse for the
                    // current room is almost always a parser miss, not an explicit
                    // "room is empty" signal. Keep the last authoritative roster;
                    // op=0297 handles real empty/leave signals.
                    Interlocked.Increment(ref _knownCount);
                    return;
                }

                emit(new PartyRosterUpdate(
                    currentRoom.GroupId,
                    currentRoom.Members,
                    ticks,
                    sourceIpv4,
                    RosterConfidence.Weak,
                    ContainsSelf: containsSelf));
                Interlocked.Increment(ref _knownCount);
                return;
            }

            LogPartyRoomList(copy0197, rooms, null);
            // Self not in this packet → it's a lobby-browser preview of OTHER
            // rooms. Don't pollute our party state — and explicitly RETURN so
            // the packet doesn't fall through to PartyMemberStatusHandler
            // (which doesn't exclude op0=0x01 from its 0x__ 0x97 catch-all)
            // and parse a random byte slice as a status-ping member, polluting
            // NicknameRegistry with garbage entityId↔nickname pairs (audit
            // 2026-05-04: B1 critical).
            Interlocked.Increment(ref _knownCount);
            return;
        }

        // 0x40 0x36 = SUMMON_SPAWN
        if (op0 == 0x40 && op1 == 0x36)
        {
            // Diagnostic: log every spawn packet's raw bytes + parse result so we
            // can see if 초월/성역 dungeon bosses come through this opcode (and just
            // fail our parser) vs. through a totally different opcode we don't yet handle.
            if (SummonSpawnLogPath != null)
            {
                try
                {
                    var hex = string.Join(" ", body.ToArray().Select(b => b.ToString("x2")));
                    bool ok = SummonSpawnHandler.TryParse(body, ticks, sourceIpv4, out var probe);
                    string parsed = ok ? $"summon={probe.SummonId} owner={probe.OwnerId} mobCode={probe.MobCode}" : "PARSE_FAIL";
                    lock (_logLock)
                    {
                        System.IO.File.AppendAllText(SummonSpawnLogPath,
                            $"{DateTime.Now:HH:mm:ss.fff} len={body.Length} {parsed}: {hex}\n");
                    }
                }
                catch { }
            }

            if (SummonSpawnHandler.TryParse(body, ticks, sourceIpv4, out var sm))
            {
                emit(sm);
                Interlocked.Increment(ref _knownCount);
            }
            else
            {
                Interlocked.Increment(ref _malformedCount);
            }
            return;
        }

        // 0x6a 0xe2 — bulk party broadcast (zone-load / dungeon entry).
        // Multi-record packet carrying every visible party member's nickname +
        // entity_id in one shot. Critical for cold-start scenarios (meter
        // started after entry): without this, party rows trickle in via OTHER_NICK
        // over the next 30+ seconds.
        if (op0 == 0x6a && op1 == 0xe2)
        {
            byte[] copy = body.ToArray();
            var bulkMembers = PartyBulkInfoHandler.ParseAll(copy, ticks, sourceIpv4);

            if (BulkInfoLogPath != null)
            {
                try
                {
                    var hex = string.Join(" ", body[..Math.Min(2048, body.Length)].ToArray().Select(b => b.ToString("x2")));
                    string srcIp = $"{(sourceIpv4 >> 24) & 0xFF}.{(sourceIpv4 >> 16) & 0xFF}.{(sourceIpv4 >> 8) & 0xFF}.{sourceIpv4 & 0xFF}";
                    var nicks = string.Join(",", bulkMembers.Select(m => $"{m.UserId}:{m.Nickname}"));
                    lock (_logLock)
                    {
                        System.IO.File.AppendAllText(BulkInfoLogPath,
                            $"{DateTime.Now:HH:mm:ss.fff} src={srcIp} parsed={bulkMembers.Count} nicks=[{nicks}] len={body.Length}: {hex}\n");
                    }
                }
                catch { }
            }

            if (bulkMembers.Count > 0)
            {
                // Emit as PartyRosterUpdate (not individual NicknameInfo) so
                // the aggregator can atomic-replace and remove anyone who's
                // no longer in the bulk roster. Previously this was add-only,
                // which left stale members visible after dungeon transitions
                // even though op=6ae2 already carried the authoritative roster.
                emit(new PartyRosterUpdate(0, bulkMembers, ticks, sourceIpv4));
                Interlocked.Increment(ref _knownCount);
            }
            else
            {
                Interlocked.Increment(ref _malformedCount);
            }
            return;
        }

        // 0x02 0x97 — matchmaking/party-assembly broadcast (lobby server).
        // Different opcode from 0x6a 0xe2 (zone-load) — fires while users are
        // still in the matchmaking room before zone change. Source IP is on
        // a different NCSoft block than the game zone server (only captured
        // once we widened the BPF filter to "tcp" wholesale).
        // Emits a single PartyRosterUpdate (not individual NicknameInfo events)
        // so the aggregator can do an atomic add/remove diff against its
        // previous party set — without batching, mid-room member add/remove
        // would only show adds (we'd never see anyone leave).
        // 0x1d 0x97 — "you left the matchmaking room" signal. A2Viewer
        // calls this "1D 97 퇴장" and uses it (plus the 1D-then-empty-01
        // sequence) as the canonical room-leave event. Without handling
        // it, the previous room's roster lingers in our meter forever
        // because no other broadcast tells us the user is no longer in
        // a room.
        if (op0 == 0x1d && op1 == 0x97)
        {
            // Diagnostic dump of every 1d97 packet so we can disambiguate the
            // sub-opcodes after the fact (empty = dissolution candidate,
            // non-empty = real exit). Logged regardless of branch taken below.
            string subtag;
            // A2Viewer's IsEmptyControlPacket (PartyStreamParser.cs:1378):
            //   packet[dataOffset] == 0 && packet[dataOffset+1] == 0
            //   && packet.Length <= dataOffset + 2
            // dataOffset = right after the 2-byte opcode, so for our `body`
            // (which includes the opcode at [0..1]) the check is body.Length<=4
            // with body[2]==0, body[3]==0.
            //
            // 사용자 보고 2026-05-03 17:53:42: 방 비공개 토글 / 제목 변경 / 입장
            // 거절 같은 host-side 룸 관리 액션에서 1d97이 발화. 이전엔 모든
            // 1d97을 즉시 PartyLeft로 처리해서 lobby-단계에서 미터가 wipe됨.
            // 이제 빈 패킷은 "해산 후보"로만 보고 fire 안 함.
            bool isEmptyControl = body.Length >= 4
                                  && body.Length <= 4
                                  && body[2] == 0
                                  && body[3] == 0;
            subtag = isEmptyControl ? "EMPTY_CANDIDATE" : "EXIT";

            if (PartyAssemblyLogPath != null)
            {
                try
                {
                    var hex = string.Join(" ", body.ToArray().Select(b => b.ToString("x2")));
                    lock (_logLock)
                    {
                        System.IO.File.AppendAllText(PartyAssemblyLogPath,
                            $"{DateTime.Now:HH:mm:ss.fff} op=1d97 {subtag} len={body.Length}: {hex}\n");
                    }
                }
                catch { }
            }

            if (isEmptyControl)
            {
                // Don't fire PartyLeft. A2Viewer treats this as a "dissolution
                // candidate" that's only confirmed by a follow-up empty 0197.
                // We don't yet implement the pending/confirmation state machine
                // — but at minimum we must NOT wipe membership on the first
                // empty 1d97, since common host-side actions (방 비공개 토글,
                // 제목 변경, 입장 거절) emit it spuriously.
                Interlocked.Increment(ref _knownCount);
                return;
            }

            // Non-empty 1d97 = explicit "you left the room" per A2Viewer's
            // [party] 1D 97 퇴장 branch. Fire PartyLeft and clear the cached
            // lobby room id so subsequent 0197 lobby-preview can't re-attach
            // the just-left room's members.
            _currentLobbyRoomId = 0;
            emit(new PartyLeft(ticks, sourceIpv4));
            Interlocked.Increment(ref _knownCount);
            return;
        }

        if (op0 == 0x02 && op1 == 0x97)
        {
            byte[] copy = body.ToArray();
            var result = PartyAssemblyHandler.ParseAll(copy, ticks, sourceIpv4);

            // Dungeon id sits between the room name and the per-member
            // records — emit it ahead of the roster update so the title
            // bar can show the current dungeon name as soon as the user
            // creates / joins a matchmaking room.
            int dungeonId = PartyAssemblyHandler.ExtractDungeonId(copy);
            if (dungeonId != 0)
                emit(new DungeonAnnouncement(dungeonId, ticks, sourceIpv4));

            // Diagnostic: dump every op=0297 packet's raw bytes + parsed
            // member count so we can correlate "X new members joined the room
            // in-game" against what our handler actually extracted. If the
            // count stays at N when the user can see N+1 in-game, the parser
            // is missing records — feed the raw hex back into the analyzer.
            if (PartyAssemblyLogPath != null)
            {
                try
                {
                    var hex = string.Join(" ", body[..Math.Min(512, body.Length)].ToArray().Select(b => b.ToString("x2")));
                    var nicks = string.Join(",", result.Members.Select(m => m.Nickname));
                    lock (_logLock)
                    {
                        System.IO.File.AppendAllText(PartyAssemblyLogPath,
                            $"{DateTime.Now:HH:mm:ss.fff} groupId={result.GroupId} len={body.Length} parsed={result.Members.Count} nicks=[{nicks}]: {hex}\n");
                    }
                }
                catch { }
            }

            // Always emit — even when Members is empty. Empty broadcasts that
            // still carry a valid groupId tell the aggregator "you left this
            // room / it's now empty" so the leaderboard wipes instead of
            // lingering on the previous roster. (Earlier `Count > 0` gate
            // dropped these signals, leaving stale members visible whenever
            // the user solo-created a room or transitioned out.)
            if (result.GroupId != 0 || result.Members.Count > 0)
            {
                if (result.GroupId != 0) _currentLobbyRoomId = result.GroupId;
                emit(new PartyRosterUpdate(result.GroupId, result.Members, ticks, sourceIpv4));
                Interlocked.Increment(ref _knownCount);
            }
            else
            {
                Interlocked.Increment(ref _malformedCount);
            }
            return;
        }

        // 0x__ 0xe2 / 0x__ 0x97 family — per-party-member status broadcast.
        // Re-enabled 2026-04-30: needed for entity_id ↔ nickname mapping (the
        // SELF_NICK / OTHER_NICK channel uses user_id space, while DAMAGE actorIds
        // live in entity_id space; this family bridges the two for visible nearby
        // players). Earlier regression (boss/mob entityIds wrongly registered as
        // players) is now mitigated by a mob_code guard in DpsAggregator's
        // NicknameInfo handler — see Aggregator OnEvent for the gate.
        if (PartyMemberStatusHandler.TryParse(body, ticks, sourceIpv4, out var partyNick))
        {
            if (LiveStatusLogPath != null)
            {
                try
                {
                    var hex = string.Join(" ", body[..Math.Min(64, body.Length)].ToArray().Select(b => b.ToString("x2")));
                    string srcIp = $"{(sourceIpv4 >> 24) & 0xFF}.{(sourceIpv4 >> 16) & 0xFF}.{(sourceIpv4 >> 8) & 0xFF}.{sourceIpv4 & 0xFF}";
                    lock (_logLock)
                    {
                        System.IO.File.AppendAllText(LiveStatusLogPath,
                            $"{DateTime.Now:HH:mm:ss.fff} op={op0:x2}{op1:x2} src={srcIp} entityId={partyNick.UserId} nick={partyNick.Nickname} server={partyNick.Server} cp={partyNick.CombatPower} len={body.Length}: {hex}\n");
                    }
                }
                catch { }
            }

            emit(partyNick);
            Interlocked.Increment(ref _knownCount);
            return;
        }

        // Unhandled (yet) — BUFF (0x2a/0x2b 0x38), party events (0x__ 0x97), etc.
        // 0x20 0x36 = suspected character profile/detail packet.
        // It is still unhandled for gameplay, but we log it specially because the
        // unknown sniffer keeps only 80 bytes and truncates the zlib body.
        if (op0 == 0x20 && op1 == 0x36)
        {
            LogProfilePacket(body);
        }

        if (body.Length >= 1000)
        {
            LogPowerHuntPacket($"UNKNOWN_{op0:x2}{op1:x2}", body, always: false);
        }

        Interlocked.Increment(ref _unknownCount);
        ushort opKey = (ushort)((op0 << 8) | op1);
        _unknownByOpcode.AddOrUpdate(opKey, 1, static (_, v) => v + 1);

        // Sniffer: dump raw bytes of first N occurrences of each unknown opcode so
        // we can reverse-engineer their layouts offline.
        if (UnknownSnifferLogPath != null)
        {
            int sampled = _unknownSampledCount.AddOrUpdate(opKey, 1, static (_, v) => v + 1);
            if (sampled <= UnknownSnifferPerOpcodeCap)
            {
                try
                {
                    // 4096-byte cap (was 512, originally 80). Some lobby/matchmaking
                    // candidates from non-zone IPs (e.g. 216.107.x.x) come in 1.7K~15K
                    // packets — at 512 we only saw the header, missing any nick info
                    // that lives further in the body. 4K covers most real candidates
                    // without exploding the log size.
                    var hex = string.Join(" ",
                        body[..Math.Min(4096, body.Length)].ToArray().Select(b => b.ToString("x2")));
                    string srcIp = $"{(sourceIpv4 >> 24) & 0xFF}.{(sourceIpv4 >> 16) & 0xFF}.{(sourceIpv4 >> 8) & 0xFF}.{sourceIpv4 & 0xFF}";
                    lock (_logLock)
                    {
                        System.IO.File.AppendAllText(UnknownSnifferLogPath,
                            $"{DateTime.Now:HH:mm:ss.fff} src={srcIp} op={op0:x2}{op1:x2} #{sampled} len={body.Length}: {hex}\n");
                    }
                }
                catch { }
            }
        }
    }
}
