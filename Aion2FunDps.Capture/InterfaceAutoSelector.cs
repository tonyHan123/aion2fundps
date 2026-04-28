using System.Net;
using System.Net.Sockets;
using SharpPcap;
using SharpPcap.LibPcap;

namespace Aion2FunDps.Capture;

public static class InterfaceAutoSelector
{
    public static LibPcapLiveDevice SelectForServer(string serverIp, int probePort = 7777)
    {
        var devices = CaptureDeviceList.Instance;
        if (devices.Count == 0)
            throw new InvalidOperationException("No capture devices found. Is Npcap installed and running?");

        IPAddress localIp;
        try
        {
            using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram,
                System.Net.Sockets.ProtocolType.Udp);
            probe.Connect(serverIp, probePort);
            localIp = ((IPEndPoint)probe.LocalEndPoint!).Address;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to probe routing for {serverIp}: {ex.Message}", ex);
        }

        var bytes = localIp.GetAddressBytes();
        if (bytes[0] == 169 && bytes[1] == 254)
            throw new InvalidOperationException(
                $"OS routed via APIPA address {localIp}. The active NIC is not connected to the internet.");

        if (localIp.Equals(IPAddress.Any))
            throw new InvalidOperationException(
                "Could not determine local IP for routing to game server.");

        foreach (var device in devices.OfType<LibPcapLiveDevice>())
        {
            if (device.Addresses.Any(a => a.Addr?.ipAddress?.Equals(localIp) == true))
                return device;
        }

        throw new InvalidOperationException(
            $"No capture device found bound to local IP {localIp}.");
    }
}
