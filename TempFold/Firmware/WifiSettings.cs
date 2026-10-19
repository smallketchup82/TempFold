// Copyright (c) 2026 smallketchup82 <ketchup@smkt.ca>. Licensed under the MIT License.
// See the LICENSE file in the repository root for full license text.

using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace TempFold.Firmware;

internal sealed record SavedWifiProfile(string Id, string Label);
internal sealed record WifiSettings(string Ssid, string Password);
internal static class HostNetwork
{
    internal static string[] LocalAddresses() => LanConnections().Select(c => c.Address).Distinct().ToArray();

    internal static BoardPort[] LanConnections() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
        .OrderByDescending(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
        .SelectMany(n => n.GetIPProperties().UnicastAddresses
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !System.Net.IPAddress.IsLoopback(a.Address) && !a.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
            .Select(a => new BoardPort(a.Address.ToString(), $"{n.Name} ({n.NetworkInterfaceType})")))
        .Distinct().ToArray();

    internal static async Task<SavedWifiProfile[]> SavedWifiProfilesAsync(CancellationToken token)
    {
        try
        {
            if (OperatingSystem.IsLinux())
            {
                string output = await ToolProcess.RunAsync("nmcli", ["-t", "-f", "UUID,TYPE", "connection", "show"], token, secretOutput: true);
                var profiles = new List<SavedWifiProfile>();
                foreach (string line in output.Split('\n').Where(l => l.EndsWith(":802-11-wireless", StringComparison.Ordinal)))
                {
                    string id = line.Split(':')[0];
                    string ssid = await WifiFieldAsync(id, "802-11-wireless.ssid", token);
                    profiles.Add(new(id, ssid + " (" + id + ")"));
                }
                return profiles.ToArray();
            }
            if (OperatingSystem.IsWindows()) return WindowsWifi.Profiles();
            if (OperatingSystem.IsMacOS())
            {
                string device = await MacWifiDeviceAsync(token);
                string output = await ToolProcess.RunAsync("networksetup", ["-listpreferredwirelessnetworks", device], token, secretOutput: true);
                return output.Split('\n').Skip(1).Select(s => s.Trim()).Where(s => s.Length > 0).Select(s => new SavedWifiProfile(s, s)).ToArray();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Spectre.Console.AnsiConsole.MarkupLine("[yellow]Saved Wi-Fi profiles are unavailable: " + Spectre.Console.Markup.Escape(ex.Message) + "[/]"); }
        return [];
    }

    private static async Task<string> WifiFieldAsync(string id, string field, CancellationToken token) =>
        (await ToolProcess.RunAsync("nmcli", ["--show-secrets", "--escape", "no", "-g", field, "connection", "show", "uuid", id], token, secretOutput: true)).TrimEnd('\r', '\n');

    private static async Task<string> MacWifiDeviceAsync(CancellationToken token)
    {
        string hardware = await ToolProcess.RunAsync("networksetup", ["-listallhardwareports"], token, secretOutput: true);
        string[] lines = hardware.Split('\n');
        int wifi = Array.FindIndex(lines, l => l.Contains("Wi-Fi", StringComparison.Ordinal));
        if (wifi < 0 || wifi + 1 >= lines.Length) throw new IOException("No Wi-Fi adapter is available; enter Wi-Fi settings manually.");
        return lines[wifi + 1].Split(':', 2).Last().Trim();
    }

    internal static async Task<WifiSettings> ReadSavedWifiAsync(string id, CancellationToken token)
    {
        if (OperatingSystem.IsWindows()) return WindowsWifi.ReadProfile(id);
        if (OperatingSystem.IsMacOS())
        {
            string password = await ToolProcess.RunAsync("security", ["find-generic-password", "-D", "AirPort network password", "-a", id, "-w"], token, secretOutput: true);
            return new(id, password.TrimEnd('\r', '\n'));
        }
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Saved profiles are unavailable; enter Wi-Fi manually.");
        string ssid = await WifiFieldAsync(id, "802-11-wireless.ssid", token);
        string security = await WifiFieldAsync(id, "802-11-wireless-security.key-mgmt", token);
        if (security is not ("" or "--" or "none" or "wpa-psk" or "sae")) throw new IOException("Enterprise Wi-Fi is unsupported; choose another profile.");
        string key = await WifiFieldAsync(id, "802-11-wireless-security.psk", token);
        if (security is "wpa-psk" or "sae" && key is "" or "--") throw new IOException("The saved password is unavailable to your account.");
        return new(ssid, key == "--" ? "" : key);
    }

    internal static async Task<WifiSettings> CopyWifiAsync(CancellationToken token)
    {
        if (OperatingSystem.IsWindows()) return WindowsWifi.Read();
        if (OperatingSystem.IsLinux())
        {
            string active = await ToolProcess.RunAsync("nmcli", ["-t", "-f", "UUID,TYPE", "connection", "show", "--active"], token, secretOutput: true);
            string? uuid = active.Split('\n').Where(l => l.EndsWith(":802-11-wireless", StringComparison.Ordinal))
                .Select(l => l.Split(':')[0]).FirstOrDefault();
            if (uuid is null) throw new IOException("No active NetworkManager Wi-Fi connection was found.");
            return await ReadSavedWifiAsync(uuid, token);
        }
        if (OperatingSystem.IsMacOS())
        {
            string hardware = await ToolProcess.RunAsync("networksetup", ["-listallhardwareports"], token, secretOutput: true);
            string[] lines = hardware.Split('\n');
            int wifi = Array.FindIndex(lines, l => l.Contains("Wi-Fi", StringComparison.Ordinal));
            string device = wifi >= 0 && wifi + 1 < lines.Length ? lines[wifi + 1].Split(':', 2).Last().Trim() : "en0";
            string network = await ToolProcess.RunAsync("networksetup", ["-getairportnetwork", device], token, secretOutput: true);
            int separator = network.IndexOf(": ", StringComparison.Ordinal);
            if (separator < 0) throw new IOException("macOS could not expose the current Wi-Fi SSID. Enter the connection manually.");
            string ssid = network[(separator + 2)..].TrimEnd('\r', '\n');
            string password = await ToolProcess.RunAsync("security", ["find-generic-password", "-D", "AirPort network password", "-a", ssid, "-w"], token, secretOutput: true);
            return new(ssid, password.TrimEnd('\r', '\n'));
        }
        throw new PlatformNotSupportedException("Copying Wi-Fi settings is unavailable on this OS; enter them manually.");
    }
}
