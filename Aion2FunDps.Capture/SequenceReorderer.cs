using System.Buffers;

namespace Aion2FunDps.Capture;

/// <summary>
/// Reorders TCP segments per 4-tuple flow (srcIP, srcPort, dstPort) using sequence numbers.
/// - Drops retransmits (seq &lt; expected).
/// - Buffers out-of-order segments and emits them when gap fills.
/// - 32-bit wrap-around safe via int diff arithmetic.
///
/// IMPORTANT: Feed() takes ownership of the RawPacket's buffer. Caller must not Dispose
/// or use the RawPacket after Feed returns.
/// </summary>
public sealed class SequenceReorderer
{
    private const int MaxHoldEntriesPerFlow = 32;
    private const int PruneIntervalPackets = 4096;
    private static readonly long IdleFlowTimeoutTicks = TimeSpan.FromMinutes(2).Ticks;

    private readonly Dictionary<ulong, FlowState> _flows = new();
    private long _droppedRetransmits;
    private long _droppedOverflow;
    private long _prunedIdleFlows;
    private int _packetsSincePrune;

    public long DroppedRetransmits => Volatile.Read(ref _droppedRetransmits);
    public long DroppedOverflow => Volatile.Read(ref _droppedOverflow);
    public long PrunedIdleFlows => Volatile.Read(ref _prunedIdleFlows);
    public int FlowCount => _flows.Count;

    public void Feed(in RawPacket packet, Action<OrderedChunk> onEmit)
    {
        if (++_packetsSincePrune >= PruneIntervalPackets)
        {
            _packetsSincePrune = 0;
            PruneIdleFlows(packet.TimestampTicks);
        }

        var flowKey = packet.FlowKey;
        if (!_flows.TryGetValue(flowKey, out var flow))
        {
            flow = new FlowState();
            _flows[flowKey] = flow;
        }
        flow.LastSeenTicks = packet.TimestampTicks;

        if (!flow.Initialized)
        {
            flow.NextExpectedSeq = packet.TcpSequence;
            flow.Initialized = true;
        }

        // 32-bit wrap-aware diff: int cast handles wrap correctly for nearby seqs.
        int diff = (int)(packet.TcpSequence - flow.NextExpectedSeq);

        if (diff < 0)
        {
            ArrayPool<byte>.Shared.Return(packet.Buffer);
            Interlocked.Increment(ref _droppedRetransmits);
            return;
        }

        if (diff == 0)
        {
            EmitChunk(flow, packet.SourceIpv4, packet.SourcePort, packet.DestinationPort,
                packet.TcpSequence, packet.TimestampTicks, packet.Buffer, packet.Length, onEmit);

            DrainHoldBuffer(flow, packet.SourceIpv4, packet.SourcePort, packet.DestinationPort,
                packet.TimestampTicks, onEmit);
            return;
        }

        if (flow.Hold.ContainsKey(packet.TcpSequence))
        {
            ArrayPool<byte>.Shared.Return(packet.Buffer);
            Interlocked.Increment(ref _droppedRetransmits);
            return;
        }

        if (flow.Hold.Count >= MaxHoldEntriesPerFlow)
        {
            ArrayPool<byte>.Shared.Return(packet.Buffer);
            Interlocked.Increment(ref _droppedOverflow);
            return;
        }

        flow.Hold[packet.TcpSequence] = new HeldPacket(packet.Buffer, packet.Length);
    }

    private static void EmitChunk(FlowState flow, uint sourceIp, ushort srcPort, ushort dstPort,
        uint seq, long ticks, byte[] buffer, int length, Action<OrderedChunk> onEmit)
    {
        onEmit(new OrderedChunk(sourceIp, srcPort, dstPort, seq, ticks, buffer, length));
        flow.NextExpectedSeq = unchecked(seq + (uint)length);
    }

    private static void DrainHoldBuffer(FlowState flow, uint sourceIp, ushort srcPort, ushort dstPort,
        long ticks, Action<OrderedChunk> onEmit)
    {
        while (flow.Hold.Count > 0)
        {
            uint firstKey = 0;
            HeldPacket firstHeld = default;
            bool found = false;
            foreach (var kv in flow.Hold)
            {
                firstKey = kv.Key;
                firstHeld = kv.Value;
                found = true;
                break;
            }
            if (!found || firstKey != flow.NextExpectedSeq) return;

            flow.Hold.Remove(firstKey);
            EmitChunk(flow, sourceIp, srcPort, dstPort, firstKey, ticks,
                firstHeld.Buffer!, firstHeld.Length, onEmit);
        }
    }

    public void Reset(ulong flowKey)
    {
        if (_flows.TryGetValue(flowKey, out var flow))
        {
            foreach (var held in flow.Hold.Values)
                ArrayPool<byte>.Shared.Return(held.Buffer);
            flow.Hold.Clear();
            flow.Initialized = false;
        }
    }

    private void PruneIdleFlows(long nowTicks)
    {
        if (_flows.Count == 0 || nowTicks <= 0) return;

        List<ulong>? stale = null;
        foreach (var (flowKey, flow) in _flows)
        {
            if (nowTicks - flow.LastSeenTicks > IdleFlowTimeoutTicks)
                (stale ??= new List<ulong>()).Add(flowKey);
        }
        if (stale == null) return;

        foreach (var flowKey in stale)
        {
            if (!_flows.TryGetValue(flowKey, out var flow)) continue;
            foreach (var held in flow.Hold.Values)
                ArrayPool<byte>.Shared.Return(held.Buffer);
            _flows.Remove(flowKey);
        }
        Interlocked.Add(ref _prunedIdleFlows, stale.Count);
    }

    private sealed class FlowState
    {
        public uint NextExpectedSeq;
        public bool Initialized;
        public long LastSeenTicks;
        public readonly SortedDictionary<uint, HeldPacket> Hold = new();
    }

    private readonly struct HeldPacket
    {
        public readonly byte[] Buffer;
        public readonly int Length;

        public HeldPacket(byte[] buffer, int length)
        {
            Buffer = buffer;
            Length = length;
        }
    }
}
