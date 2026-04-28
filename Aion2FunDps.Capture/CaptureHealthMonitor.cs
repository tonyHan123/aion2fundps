using SharpPcap.LibPcap;

namespace Aion2FunDps.Capture;

public sealed class CaptureHealthMonitor : IDisposable
{
    private readonly LibPcapLiveDevice _device;
    private readonly Timer _timer;
    private long _lastDropped;
    private long _lastIfaceDropped;
    private long _droppedAtChannel;

    public long ReceivedPackets { get; private set; }
    public long DroppedPackets { get; private set; }
    public long InterfaceDroppedPackets { get; private set; }
    public long DroppedAtChannel => Volatile.Read(ref _droppedAtChannel);

    public event EventHandler<DropDetectedEventArgs>? DropDetected;

    public CaptureHealthMonitor(LibPcapLiveDevice device)
    {
        _device = device;
        _timer = new Timer(Poll, null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Start() => _timer.Change(1000, 1000);
    public void Stop() => _timer.Change(Timeout.Infinite, Timeout.Infinite);

    public void IncrementChannelDrop() => Interlocked.Increment(ref _droppedAtChannel);

    private void Poll(object? state)
    {
        try
        {
            var stats = _device.Statistics;
            ReceivedPackets = (long)stats.ReceivedPackets;
            DroppedPackets = (long)stats.DroppedPackets;
            InterfaceDroppedPackets = (long)stats.InterfaceDroppedPackets;

            var newDrops = (DroppedPackets - _lastDropped) + (InterfaceDroppedPackets - _lastIfaceDropped);
            if (newDrops > 0)
            {
                DropDetected?.Invoke(this,
                    new DropDetectedEventArgs(newDrops, DroppedPackets, InterfaceDroppedPackets));
            }

            _lastDropped = DroppedPackets;
            _lastIfaceDropped = InterfaceDroppedPackets;
        }
        catch
        {
            // Stats poll failure is non-fatal; capture continues.
        }
    }

    public void Dispose() => _timer.Dispose();
}

public sealed class DropDetectedEventArgs(long newDrops, long totalKernel, long totalInterface) : EventArgs
{
    public long NewDrops { get; } = newDrops;
    public long TotalKernelDrops { get; } = totalKernel;
    public long TotalInterfaceDrops { get; } = totalInterface;
}
