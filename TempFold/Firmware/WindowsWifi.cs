// Copyright (c) 2026 smallketchup82 <ketchup@smkt.ca>. Licensed under the MIT License.
// See the LICENSE file in the repository root for full license text.

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Xml.Linq;

namespace TempFold.Firmware;

[SupportedOSPlatform("windows")]
internal static class WindowsWifi
{
    // Read only the currently connected profile. The OS may deny plaintext-key
    // access; never elevate silently or fall back to exporting every profile.
    internal static WifiSettings Read()
    {
        Check(WlanOpenHandle(2, IntPtr.Zero, out _, out IntPtr handle));
        try
        {
            Check(WlanEnumInterfaces(handle, IntPtr.Zero, out IntPtr list));
            try
            {
                int count = Marshal.ReadInt32(list);
                for (int i = 0; i < count; ++i)
                {
                    IntPtr item = list + 8 + i * 532;
                    if (Marshal.ReadInt32(item + 528) != 1) continue; // connected
                    Guid id = Marshal.PtrToStructure<Guid>(item);
                    Check(WlanQueryInterface(handle, ref id, 7, IntPtr.Zero, out _, out IntPtr connection, out _));
                    string profile;
                    string ssid;
                    try
                    {
                        profile = Marshal.PtrToStringUni(connection + 8, 256)!.TrimEnd('\0');
                        int length = Marshal.ReadInt32(connection + 520);
                        if (length is < 1 or > 32) throw new IOException("Invalid Wi-Fi SSID returned by Windows.");
                        byte[] bytes = new byte[length]; Marshal.Copy(connection + 524, bytes, 0, length);
                        ssid = Encoding.UTF8.GetString(bytes);
                    }
                    finally { WlanFreeMemory(connection); }
                    uint flags = 4; // WLAN_PROFILE_GET_PLAINTEXT_KEY
                    Check(WlanGetProfile(handle, ref id, profile, IntPtr.Zero, out IntPtr xml, ref flags, out _));
                    try
                    {
                        XDocument doc = XDocument.Parse(Marshal.PtrToStringUni(xml)!);
                        string Value(string name) => doc.Descendants().FirstOrDefault(e => e.Name.LocalName == name)?.Value ?? "";
                        if (Value("useOneX") == "true") throw new IOException("Enterprise Wi-Fi is not supported by TFirm.");
                        string authentication = Value("authentication");
                        if (authentication == "open" && Value("encryption") == "none") return new(ssid, "");
                        if (Value("protected") != "false" || string.IsNullOrEmpty(Value("keyMaterial")))
                            throw new IOException("Windows did not permit reading the saved Wi-Fi password. Enter it manually.");
                        if (authentication is not ("WPAPSK" or "WPA2PSK" or "WPA3SAE"))
                            throw new IOException("This Wi-Fi authentication mode is not supported by TFirm.");
                        return new(ssid, Value("keyMaterial"));
                    }
                    finally { WlanFreeMemory(xml); }
                }
            }
            finally { WlanFreeMemory(list); }
        }
        finally { WlanCloseHandle(handle, IntPtr.Zero); }
        throw new IOException("No connected Wi-Fi interface was found.");
    }
    internal static SavedWifiProfile[] Profiles()
    {
        Check(WlanOpenHandle(2, IntPtr.Zero, out _, out IntPtr handle));
        try
        {
            Check(WlanEnumInterfaces(handle, IntPtr.Zero, out IntPtr list));
            try
            {
                var profiles = new List<SavedWifiProfile>();
                for (int i = 0; i < Marshal.ReadInt32(list); ++i)
                {
                    Guid id = Marshal.PtrToStructure<Guid>(list + 8 + i * 532);
                    Check(WlanGetProfileList(handle, ref id, IntPtr.Zero, out IntPtr names));
                    try
                    {
                        for (int j = 0; j < Marshal.ReadInt32(names); ++j)
                        {
                            string name = Marshal.PtrToStringUni(names + 8 + j * 516, 256)!.TrimEnd('\0');
                            profiles.Add(new(id + "|" + name, name + " (" + id + ")"));
                        }
                    }
                    finally { WlanFreeMemory(names); }
                }
                return profiles.ToArray();
            }
            finally { WlanFreeMemory(list); }
        }
        finally { WlanCloseHandle(handle, IntPtr.Zero); }
    }

    internal static WifiSettings ReadProfile(string selection)
    {
        string[] parts = selection.Split('|', 2);
        Guid id = Guid.Parse(parts[0]);
        Check(WlanOpenHandle(2, IntPtr.Zero, out _, out IntPtr handle));
        try
        {
            uint flags = 4;
            Check(WlanGetProfile(handle, ref id, parts[1], IntPtr.Zero, out IntPtr xml, ref flags, out _));
            try
            {
                var doc = XDocument.Parse(Marshal.PtrToStringUni(xml)!);
                string Value(string name) => doc.Descendants().FirstOrDefault(e => e.Name.LocalName == name)?.Value ?? "";
                string ssid = doc.Descendants().First(e => e.Name.LocalName == "SSID").Descendants().First(e => e.Name.LocalName == "name").Value;
                if (Value("useOneX") == "true") throw new IOException("Enterprise Wi-Fi is unsupported.");
                if (Value("authentication") == "open" && Value("encryption") == "none") return new(ssid, "");
                if (Value("authentication") is not ("WPAPSK" or "WPA2PSK" or "WPA3SAE") || Value("protected") != "false" || Value("keyMaterial").Length == 0)
                    throw new IOException("Windows cannot supply a supported plaintext key for this selected profile; enter it manually.");
                return new(ssid, Value("keyMaterial"));
            }
            finally { WlanFreeMemory(xml); }
        }
        finally { WlanCloseHandle(handle, IntPtr.Zero); }
    }

    [DllImport("wlanapi.dll")] private static extern uint WlanGetProfileList(IntPtr handle, ref Guid id, IntPtr reserved, out IntPtr list);
    private static void Check(uint error)
    {
        if (error != 0) throw new IOException($"Windows could not read current Wi-Fi settings (error {error}). Enter them manually.");
    }
    [DllImport("wlanapi.dll")] private static extern uint WlanOpenHandle(uint version, IntPtr reserved, out uint negotiated, out IntPtr handle);
    [DllImport("wlanapi.dll")] private static extern uint WlanCloseHandle(IntPtr handle, IntPtr reserved);
    [DllImport("wlanapi.dll")] private static extern uint WlanEnumInterfaces(IntPtr handle, IntPtr reserved, out IntPtr list);
    [DllImport("wlanapi.dll")] private static extern void WlanFreeMemory(IntPtr memory);
    [DllImport("wlanapi.dll")] private static extern uint WlanQueryInterface(IntPtr handle, ref Guid id, uint opcode, IntPtr reserved, out uint size, out IntPtr data, out uint valueType);
    [DllImport("wlanapi.dll", CharSet = CharSet.Unicode)] private static extern uint WlanGetProfile(IntPtr handle, ref Guid id, string name, IntPtr reserved, out IntPtr xml, ref uint flags, out uint access);
}
