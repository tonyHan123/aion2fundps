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

    public NpcapAdapter(CaptureOptions options)
    {
        _device = InterfaceAutoSelector.SelectForServer(options.ServerIp);

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

        if (!TryExtractTcpPayload(data,
                out uint sourceIp,
                out uint tcpSeq,
                out int payloadOffset,
                out int payloadLength))
            return;

        var rented = ArrayPool<byte>.Shared.Rent(payloadLength);
        data.Slice(payloadOffset, payloadLength).CopyTo(rented);

        var packet = new RawPacket(sourceIp, tcpSeq, raw.Timeval.Date.Ticks, rented, payloadLength);

        if (!_writer.TryWrite(packet))
        {
            ArrayPool<byte>.Shared.Return(rented);
            Health.IncrementChannelDrop();
        }
    }

    private static bool TryExtractTcpPayload(
        ReadOnlySpan<byte> packet,
        out uint sourceIp,
        out uint tcpSeq,
        out int payloadOffset,
        out int payloadLength)
    {
        sourceIp = 0;
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

        sourceIp = (uint)((packet[ipStart + 12] << 24)
                       | (packet[ipStart + 13] << 16)
                       | (packet[ipStart + 14] << 8)
                       |  packet[ipStart + 15]);

        int tcpStart = ipStart + ipHeaderLen;
        if (packet.Length < tcpStart + 20) return false;

        tcpSeq = (uint)((packet[tcpStart + 4] << 24)
                     | (packet[tcpStart + 5] << 16)
                     | (packet[tcpStart + 6] << 8)
                     |  packet[tcpStart + 7]);

        int tcpHeaderLen = (packet[tcpStart + 12] >> 4) * 4;
        if (tcpHeaderLen < 20 || packet.Length < tcpStart + tcpHeaderLen) return false;

        payloadOffset = tcpStart + tcpHeaderLen;
        payloadLength = packet.Length - payloadOffset;

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
