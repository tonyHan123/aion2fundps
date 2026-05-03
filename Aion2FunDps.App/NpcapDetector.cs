using System.IO;
using Microsoft.Win32;

namespace Aion2FunDps.App;

/// <summary>
/// Detects whether Npcap (the WinPcap-API-compatible packet capture driver
/// from the Nmap Project) is installed on the local machine. We require it
/// because SharpPcap loads <c>wpcap.dll</c> at startup; without Npcap, our
/// capture layer fails to init and the meter has nothing to measure.
///
/// Why a dedicated detector instead of just trying to capture and catching:
/// SharpPcap's failure mode on missing Npcap is a low-level
/// <see cref="System.IO.FileNotFoundException"/> deep inside libpcap's
/// init path. We surface a clean dialog instead — same UX as Wireshark.
///
/// Why we don't bundle / silently install Npcap: free Npcap's license
/// (npcap.com/oem/redist) prohibits redistribution and reserves the silent
/// installer for paid OEM customers. We point the user to the official
/// download page and let them install through Npcap's own UI.
/// </summary>
public static class NpcapDetector
{
    public static bool IsInstalled() =>
        FileLoaderCheck() || RegistryCheck() || SystemDirCheck();

    /// <summary>
    /// Direct check: does <c>wpcap.dll</c> exist where SharpPcap will look
    /// for it? Npcap's installer drops a stub at System32 and the real DLL
    /// at System32\Npcap\. The DLL existing is the strongest signal — even
    /// if registry got cleaned up, if the file is there libpcap will load.
    /// </summary>
    private static bool FileLoaderCheck()
    {
        var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var npcapDll = Path.Combine(system32, "Npcap", "wpcap.dll");
        return File.Exists(npcapDll);
    }

    /// <summary>
    /// Registry check: Npcap's installer writes its uninstall entry under
    /// HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\NpcapInst.
    /// Belt-and-suspenders against partial uninstalls that left the DLL
    /// path on disk but broke the runtime registration.
    /// </summary>
    private static bool RegistryCheck()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\NpcapInst");
            return key != null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Last resort: System32\Packet.dll exists. Older WinPcap-style installs
    /// dropped this directly. Modern Npcap installs it under System32\Npcap.
    /// Catches edge cases where neither of the above check paths succeed but
    /// the driver is in fact loadable.
    /// </summary>
    private static bool SystemDirCheck()
    {
        var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        return File.Exists(Path.Combine(system32, "Npcap", "Packet.dll"))
            || File.Exists(Path.Combine(system32, "Packet.dll"));
    }
}
