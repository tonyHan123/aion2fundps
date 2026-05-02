using System.Buffers;
using System.Threading.Channels;
using SharpPcap;
using SharpPcap.LibPcap;

namespace Aion2FunDps.Capture;

public sealed class NpcapAdapter : IDisposable
{
    private readonly LibPcapLiveDevice _device;
    private readonly Channel<RawPacket> _channel;
    private readonly ChannelWriter<RawPacket> _writer;
    private bool _started;
    private bool _disposed;

    public ChannelReader<RawPacket> Reader { get; }
    public CaptureHealthMonitor Health { get; }
    public string SelectedInterface => _device.Description;
    /// <summary>Diagnostic log file for UDP payloads. When set, every UDP packet's
    /// source IP + ports + raw payload (first ~256 byte hex) is dumped here for
    /// post-hoc analysis — used to discover join/leave event packets that
    /// matchmaking lobbies broadcast over UDP rather than TCP.</summary>
    public string? UdpDumpPath { get; set; }
    private readonly object _udpLogLock = new();

    public NpcapAdapter(CaptureOptions options)
    {
        _device = InterfaceAutoSelector.SelectForServer(options.RoutingProbeIp);

        _channel = Channel.CreateBounded<RawPacket>(new BoundedChannelOptions(options.ChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
        });
        Reader = _channel.Reader;
        _writer = _channel.Writer;

        _device.Open(new DeviceConfiguration
        {
            Mode = DeviceModes.None,
            ReadTimeout = options.ReadTimeoutMs,
            Snaplen = options.SnapshotLengthBytes,
            BufferSize = options.BufferSizeBytes,
        });
        _device.Filter = options.BpfFilter;
        _device.OnPacketArrival += OnPacket;

        Health = new CaptureHealthMonitor(_device);
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started) throw new InvalidOperationException("Already started.");
        _started = true;
        _device.StartCapture();
        Health.Start();
    }

    public void Stop()
    {
        if (!_started) return;
        _started = false;
        _device.StopCapture();
        Health.Stop();
        _writer.TryComplete();
    }

    private void OnPacket(object sender, PacketCapture e)
    {
        var raw = e.GetPacket();
        var data = raw.Data.AsSpan();

        // UDP dump path: BPF filter passes both TCP and UDP. UDP gets logged
        // (raw bytes for offline analysis) but not piped through the TCP frame
        // assembler — UDP framing is different (datagram-per-packet, no TCP
        // sequence reordering needed).
        if (UdpDumpPath != null && data.Length >= 28
            && data[12] == 0x08 && data[13] == 0x00 && data[14 + 9] == 17)
        {
            DumpUdp(data, raw.Timeval.Date);
        }

        if (!TryExtractTcpPayload(data,
                out uint sourceIp,
                out ushort srcPort,
                out ushort dstPort,
                out uint tcpSeq,
                out int payloadOffset,
                out int payloadLength))
            return;

        var rented = ArrayPool<byte>.Shared.Rent(payloadLength);
        data.Slice(payloadOffset, payloadLength).CopyTo(rented);

        var packet = new RawPacket(sourceIp, srcPort, dstPort, tcpSeq,
            raw.Timeval.Date.Ticks, rented, payloadLength);

        if (!_writer.TryWrite(packet))
        {
            ArrayPool<byte>.Shared.Return(rented);
            Health.IncrementChannelDrop();
        }
    }

    private void DumpUdp(ReadOnlySpan<byte> packet, DateTime timestamp)
    {
        const int ipStart = 14;
        byte vhl = packet[ipStart];
        if ((vhl >> 4) != 4) return;
        int ipHeaderLen = (vhl & 0x0F) * 4;
        if (packet.Length < ipStart + ipHeaderLen + 8) return;

        int ipTotalLen = (packet[ipStart + 2] << 8) | packet[ipStart + 3];
        int ipEnd = Math.Min(packet.Length, ipStart + ipTotalLen);

        uint sourceIp = (uint)((packet[ipStart + 12] << 24)
                             | (packet[ipStart + 13] << 16)
                             | (packet[ipStart + 14] << 8)
                             |  packet[ipStart + 15]);

        int udpStart = ipStart + ipHeaderLen;
        ushort srcPort = (ushort)((packet[udpStart] << 8) | packet[udpStart + 1]);
        ushort dstPort = (ushort)((packet[udpStart + 2] << 8) | packet[udpStart + 3]);
        int payloadOffset = udpStart + 8;
        int payloadLength = Math.Max(0, ipEnd - payloadOffset);
        if (payloadLength == 0) return;

        int dumpLen = Math.Min(256, payloadLength);
        var sb = new System.Text.StringBuilder(dumpLen * 3 + 64);
        sb.Append(timestamp.ToString("HH:mm:ss.fff"));
        sb.Append($" srcIp={(sourceIp >> 24) & 0xFF}.{(sourceIp >> 16) & 0xFF}.{(sourceIp >> 8) & 0xFF}.{sourceIp & 0xFF}");
        sb.Append($" src={srcPort} dst={dstPort} len={payloadLength}: ");
        for (int i = 0; i < dumpLen; i++)
        {
            sb.Append(packet[payloadOffset + i].ToString("x2"));
            sb.Append(' ');
        }
        sb.Append('\n');

        try
        {
            lock (_udpLogLock)
            {
                System.IO.File.AppendAllText(UdpDumpPath!, sb.ToString());
            }
        }
        catch { }
    }

    private static bool TryExtractTcpPayload(
        ReadOnlySpan<byte> packet,
        out uint sourceIp,
        out ushort sourcePort,
        out ushort destinationPort,
        out uint tcpSeq,
        out int payloadOffset,
        out int payloadLength)
    {
        sourceIp = 0;
        sourcePort = 0;
        destinationPort = 0;
        tcpSeq = 0;
        payloadOffset = 0;
        payloadLength = 0;

        if (packet.Length < 54) return false;
        if (packet[12] != 0x08 || packet[13] != 0x00) return false;

        const int ipStart = 14;
        byte vhl = packet[ipStart];
        if ((vhl >> 4) != 4) return false;
        int ipHeaderLen = (vhl & 0x0F) * 4;
        if (ipHeaderLen < 20 || packet.Length < ipStart + ipHeaderLen) return false;
        if (packet[ipStart + 9] != 6) return false;

        // IP Total Length (offset 2-3, big-endian) — authoritative IP datagram length.
        // We trust this rather than packet.Length, because Ethernet pads sub-60-byte frames
        // with zeros that would otherwise be misread as TCP payload, corrupting seq tracking.
        int ipTotalLen = (packet[ipStart + 2] << 8) | packet[ipStart + 3];
        if (ipTotalLen < ipHeaderLen + 20) return false;
        int ipEnd = ipStart + ipTotalLen;
        if (packet.Length < ipEnd) return false;

        sourceIp = (uint)((packet[ipStart + 12] << 24)
                       | (packet[ipStart + 13] << 16)
                       | (packet[ipStart + 14] << 8)
                       |  packet[ipStart + 15]);

        int tcpStart = ipStart + ipHeaderLen;
        if (ipEnd < tcpStart + 20) return false;

        sourcePort = (ushort)((packet[tcpStart] << 8) | packet[tcpStart + 1]);
        destinationPort = (ushort)((packet[tcpStart + 2] << 8) | packet[tcpStart + 3]);

        tcpSeq = (uint)((packet[tcpStart + 4] << 24)
                     | (packet[tcpStart + 5] << 16)
                     | (packet[tcpStart + 6] << 8)
                     |  packet[tcpStart + 7]);

        int tcpHeaderLen = (packet[tcpStart + 12] >> 4) * 4;
        if (tcpHeaderLen < 20 || ipEnd < tcpStart + tcpHeaderLen) return false;

        payloadOffset = tcpStart + tcpHeaderLen;
        payloadLength = ipEnd - payloadOffset;

        return payloadLength > 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _device.OnPacketArrival -= OnPacket;
        _device.Dispose();
        Health.Dispose();
    }
}
