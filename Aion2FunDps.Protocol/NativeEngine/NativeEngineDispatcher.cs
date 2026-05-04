using System.Runtime.InteropServices;
using System.Text;
using Aion2FunDps.Capture;
using Aion2FunDps.Core;
using Aion2FunDps.Core.Models;
// Note: deliberately NOT `using static NativeEngineInterop` — the POD
// struct names inside that class collide with Core.Models record names
// (DamageEvent / NicknameInfo / etc.). Explicit `NEI.Xxx` references
// disambiguate per call site without alias gymnastics elsewhere.
using NEI = Aion2FunDps.Protocol.NativeEngine.NativeEngineInterop;

namespace Aion2FunDps.Protocol.NativeEngine;

/// <summary>
/// Managed adapter around <c>Aion2FunDps.Engine.dll</c>. Same surface as
/// <see cref="PacketDispatcher"/> from the consumer's POV (Dispatch
/// fires <see cref="IGameEvent"/> via an emit callback) but the parsing
/// runs in native C++ — see Aion2FunDps.Engine/src/ for the
/// implementation.
///
/// Lifecycle:
///   - Constructor allocates a native dispatcher handle, registers all
///     event callbacks, and pins the delegate references in
///     <see cref="_pinnedDelegates"/> so the GC doesn't collect them
///     while native code holds the function pointers.
///   - <see cref="Dispatch"/> hands a packet body off to the native
///     dispatcher; callbacks fire synchronously on the calling thread.
///   - <see cref="Dispose"/> frees the native handle and releases the
///     pinned delegates. Forgetting to dispose leaks the handle until
///     finalization (rare path; finalizer is the safety net).
///
/// Threading: same contract as managed PacketDispatcher — callers
/// serialize Dispatch() calls. Native callbacks fire on the calling
/// thread, so the emit callback executes inline.
/// </summary>
public sealed class NativeEngineDispatcher : IDisposable, IDispatcherTelemetry
{
    private IntPtr _handle;
    private readonly Action<IGameEvent> _emit;

    // GC-pinning slot. .NET delegates passed to unmanaged code MUST be
    // kept alive by managed references — otherwise the GC may collect
    // them while native code still has the function pointer, leading to
    // an access violation on the next callback. Holding them in this
    // list ensures they outlive the native handle.
    private readonly List<Delegate> _pinnedDelegates = new();

    public NativeEngineDispatcher(Action<IGameEvent> emit)
    {
        _emit = emit ?? throw new ArgumentNullException(nameof(emit));
        _handle = NEI.Aion2Fun_Dispatcher_Create();
        if (_handle == IntPtr.Zero)
            throw new InvalidOperationException("Aion2Fun_Dispatcher_Create returned null");

        WireCallbacks();
    }

    private void WireCallbacks()
    {
        // Each registration: instantiate a delegate that closes over
        // `_emit`, pin it, then pass to the native setter. The setters
        // store the function pointer; subsequent dispatches invoke it.

        NEI.OnDamage onDamage = (IntPtr ctx, ref NEI.DamageEvent e) =>
        {
            _emit(new Core.Models.DamageEvent(
                ActorId:        e.ActorId,
                TargetId:       e.TargetId,
                Damage:         e.Damage,
                SkillCode:      e.SkillCode,
                Type:           e.Type,
                Specials:       (SpecialDamageFlags)e.Specials,
                Loop:           e.Loop,
                IsDot:          e.IsDot != 0,
                TimestampTicks: e.TimestampTicks,
                SourceIpv4:     e.SourceIpv4));
        };
        _pinnedDelegates.Add(onDamage);
        NEI.Aion2Fun_Dispatcher_SetOnDamage(_handle, onDamage);

        NEI.OnMobHp onMobHp = (IntPtr ctx, ref NEI.MobHpUpdate e) =>
        {
            _emit(new Core.Models.MobHpUpdate(
                MobId:          e.MobId,
                CurrentHp:      e.CurrentHp,
                TimestampTicks: e.TimestampTicks,
                SourceIpv4:     e.SourceIpv4));
        };
        _pinnedDelegates.Add(onMobHp);
        NEI.Aion2Fun_Dispatcher_SetOnMobHp(_handle, onMobHp);

        NEI.OnEncounter onEncounter = (IntPtr ctx, ref NEI.EncounterAnnouncement e) =>
        {
            _emit(new Core.Models.EncounterAnnouncement(
                MobCode:        e.MobCode,
                TimestampTicks: e.TimestampTicks,
                SourceIpv4:     e.SourceIpv4));
        };
        _pinnedDelegates.Add(onEncounter);
        NEI.Aion2Fun_Dispatcher_SetOnEncounter(_handle, onEncounter);

        NEI.OnCombatBoundary onCombatBoundary = (IntPtr ctx, ref NEI.CombatBoundary e) =>
        {
            _emit(new Core.Models.CombatBoundary(
                MobId:          e.MobId,
                TimestampTicks: e.TimestampTicks,
                SourceIpv4:     e.SourceIpv4));
        };
        _pinnedDelegates.Add(onCombatBoundary);
        NEI.Aion2Fun_Dispatcher_SetOnCombatBoundary(_handle, onCombatBoundary);

        NEI.OnNickname onNickname = (IntPtr ctx, ref NEI.NicknameInfo e) =>
        {
            string nickname = MarshalUtf8(e.Nickname, e.NicknameLen);
            _emit(new Core.Models.NicknameInfo(
                UserId:         e.UserId,
                Nickname:       nickname,
                IsSelf:         e.IsSelf != 0,
                Server:         e.Server,
                Job:            e.Job,
                CombatPower:    e.CombatPower,
                TimestampTicks: e.TimestampTicks,
                SourceIpv4:     e.SourceIpv4,
                IsPartyMember:  e.IsPartyMember != 0,
                IsRosterStart:  e.IsRosterStart != 0,
                GroupId:        e.RoomId));
        };
        _pinnedDelegates.Add(onNickname);
        NEI.Aion2Fun_Dispatcher_SetOnNickname(_handle, onNickname);

        NEI.OnSummonSpawn onSummonSpawn = (IntPtr ctx, ref NEI.SummonSpawnInfo e) =>
        {
            string? ownerName = e.OwnerName != IntPtr.Zero
                ? MarshalUtf8(e.OwnerName, e.OwnerNameLen)
                : null;
            _emit(new Core.Models.SummonSpawnInfo(
                SummonId:       e.SummonId,
                OwnerId:        e.OwnerId,
                MobCode:        e.MobCode,
                TimestampTicks: e.TimestampTicks,
                SourceIpv4:     e.SourceIpv4,
                OwnerName:      ownerName));
        };
        _pinnedDelegates.Add(onSummonSpawn);
        NEI.Aion2Fun_Dispatcher_SetOnSummonSpawn(_handle, onSummonSpawn);

        NEI.OnCombatPower onCombatPower = (IntPtr ctx, ref NEI.CombatPowerUpdate e) =>
        {
            _emit(new Core.Models.CombatPowerUpdate(
                Nickname:       MarshalUtf8(e.Nickname, e.NicknameLen),
                ServerId:       e.ServerId,
                CombatPower:    e.CombatPower,
                TimestampTicks: e.TimestampTicks,
                SourceIpv4:     e.SourceIpv4));
        };
        _pinnedDelegates.Add(onCombatPower);
        NEI.Aion2Fun_Dispatcher_SetOnCombatPower(_handle, onCombatPower);

        NEI.OnPartyLeft onPartyLeft = (IntPtr ctx, ref NEI.PartyLeft e) =>
        {
            _emit(new Core.Models.PartyLeft(
                TimestampTicks: e.TimestampTicks,
                SourceIpv4:     e.SourceIpv4));
        };
        _pinnedDelegates.Add(onPartyLeft);
        NEI.Aion2Fun_Dispatcher_SetOnPartyLeft(_handle, onPartyLeft);

        NEI.OnDungeon onDungeon = (IntPtr ctx, ref NEI.DungeonAnnouncement e) =>
        {
            _emit(new Core.Models.DungeonAnnouncement(
                DungeonId:      e.DungeonId,
                TimestampTicks: e.TimestampTicks,
                SourceIpv4:     e.SourceIpv4));
        };
        _pinnedDelegates.Add(onDungeon);
        NEI.Aion2Fun_Dispatcher_SetOnDungeon(_handle, onDungeon);
    }

    /// <summary>
    /// Hand a complete game-packet body off to the native dispatcher.
    /// Same contract as <see cref="PacketDispatcher.Dispatch"/>: events
    /// fire synchronously via the emit callback registered at construction.
    /// </summary>
    public void Dispatch(in GamePacket gp)
    {
        if (_handle == IntPtr.Zero) return;

        var data = gp.Data;
        if (data.Length == 0) return;

        // Pin the managed buffer for the duration of the native call.
        // Buffer is owned by the SharpPcap packet pool — we only borrow
        // it for the dispatch call.
        unsafe
        {
            fixed (byte* p = data)
            {
                NEI.Aion2Fun_Dispatcher_Dispatch(
                    _handle, (IntPtr)p, data.Length,
                    gp.SourceIpv4, gp.TimestampTicks);
            }
        }
    }

    public long MalformedCount  => _handle != IntPtr.Zero ? NEI.Aion2Fun_Dispatcher_MalformedCount(_handle)  : 0;
    public long Lz4SuccessCount => _handle != IntPtr.Zero ? NEI.Aion2Fun_Dispatcher_Lz4SuccessCount(_handle) : 0;
    public long Lz4FailureCount => _handle != IntPtr.Zero ? NEI.Aion2Fun_Dispatcher_Lz4FailureCount(_handle) : 0;

    // Telemetry surfaces not (yet) tracked natively — see IDispatcherTelemetry
    // doc for rationale. UI debug texts that read these will show 0/empty in
    // native mode; this is intentional and documented.
    public long UnknownCount => 0;
    public IReadOnlyDictionary<ushort, long> UnknownByOpcode { get; } =
        new Dictionary<ushort, long>();
    public long SelfNickSeen    => 0;
    public long SelfNickParsed  => 0;
    public long OtherNickSeen   => 0;
    public long OtherNickParsed => 0;

    /// <summary>
    /// Marshals a `(IntPtr ptr, int len)` pair pointing at UTF-8 bytes
    /// inside the native packet buffer into a managed string. The
    /// pointer is only valid during the callback that emitted it; we
    /// allocate a string copy here so the managed event object can be
    /// safely retained.
    /// </summary>
    private static string MarshalUtf8(IntPtr ptr, int len)
    {
        if (ptr == IntPtr.Zero || len <= 0) return string.Empty;
        unsafe
        {
            return Encoding.UTF8.GetString((byte*)ptr.ToPointer(), len);
        }
    }

    private bool _disposed;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_handle != IntPtr.Zero)
        {
            NEI.Aion2Fun_Dispatcher_Destroy(_handle);
            _handle = IntPtr.Zero;
        }
        // Drop the delegate roots so the GC can reclaim them.
        _pinnedDelegates.Clear();
        GC.SuppressFinalize(this);
    }

    ~NativeEngineDispatcher()
    {
        // Safety net for callers who forget to dispose. Native handle
        // must be released to avoid leaking the C++ allocation across
        // app sessions — particularly important because the meter is
        // long-running.
        if (_handle != IntPtr.Zero)
        {
            NEI.Aion2Fun_Dispatcher_Destroy(_handle);
            _handle = IntPtr.Zero;
        }
    }
}
