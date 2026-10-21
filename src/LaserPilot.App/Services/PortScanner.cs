using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using LaserPilot.Core.Comm;

namespace LaserPilot.App.Services;

/// <summary>A COM port with its Windows device name, e.g. "COM5 — USB-SERIAL CH340".</summary>
public sealed record PortInfo(string Name, string Description, bool LikelyLaser)
{
    public override string ToString() =>
        Description.Length > 0 ? $"{Name} — {Description}{(LikelyLaser ? "  ★" : "")}" : Name;
}

/// <summary>
/// Lists serial ports with their device descriptions (SetupAPI), and flags the
/// USB-serial chips laser controllers use — the B10 shows up as a CH340.
/// </summary>
public static class PortScanner
{
    public static List<PortInfo> Scan()
    {
        var names = GrblController.ListPorts();
        var devices = new Dictionary<string, (string Description, string HardwareId)>(StringComparer.OrdinalIgnoreCase);
        try { EnumerateDevices(devices); } catch { /* fall back to bare names */ }

        return names.Select(name =>
        {
            devices.TryGetValue(name, out var d);
            var desc = d.Description ?? "";
            return new PortInfo(name, desc, IsLikelyLaser(desc, d.HardwareId ?? ""));
        }).ToList();
    }

    private static bool IsLikelyLaser(string description, string hardwareId)
    {
        var hw = hardwareId.ToUpperInvariant();
        if (hw.Contains("VID_1A86") || hw.Contains("VID_10C4") || hw.Contains("VID_0403") ||
            hw.Contains("VID_2341") || hw.Contains("VID_2A03") || hw.Contains("VID_303A"))
            return true;
        var d = description.ToUpperInvariant();
        return d.Contains("CH340") || d.Contains("CH341") || d.Contains("CH910") || d.Contains("CP210") ||
               d.Contains("USB-SERIAL") || d.Contains("USB SERIAL") || d.Contains("ARDUINO");
    }

    // ---- SetupAPI ----

    private static readonly Guid PortsClass = new("4d36e978-e325-11ce-bfc1-08002be10318");
    private const uint DigcfPresent = 0x2;
    private const uint SpdrpFriendlyName = 0x0C;
    private const uint SpdrpHardwareId = 0x01;
    private const uint DicsFlagGlobal = 1;
    private const uint DiregDev = 1;
    private const int KeyRead = 0x20019;

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevinfoData
    {
        public uint CbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref SpDevinfoData data);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceRegistryPropertyW(IntPtr set, ref SpDevinfoData data, uint property,
        out uint regType, byte[]? buffer, uint bufferSize, out uint requiredSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiOpenDevRegKey(IntPtr set, ref SpDevinfoData data, uint scope, uint hwProfile, uint keyType, int samDesired);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    private static void EnumerateDevices(Dictionary<string, (string, string)> result)
    {
        var guid = PortsClass;
        var set = SetupDiGetClassDevsW(ref guid, IntPtr.Zero, IntPtr.Zero, DigcfPresent);
        if (set == IntPtr.Zero || set == new IntPtr(-1)) return;
        try
        {
            var data = new SpDevinfoData { CbSize = (uint)Marshal.SizeOf<SpDevinfoData>() };
            for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref data); i++)
            {
                var portName = ReadPortName(set, ref data);
                if (string.IsNullOrEmpty(portName)) continue;
                var friendly = ReadStringProperty(set, ref data, SpdrpFriendlyName);
                var hardwareId = ReadStringProperty(set, ref data, SpdrpHardwareId);
                // "USB-SERIAL CH340 (COM5)" → "USB-SERIAL CH340"
                var suffix = $"({portName})";
                if (friendly.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    friendly = friendly[..^suffix.Length].TrimEnd();
                result[portName] = (friendly, hardwareId);
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
    }

    private static string ReadStringProperty(IntPtr set, ref SpDevinfoData data, uint property)
    {
        SetupDiGetDeviceRegistryPropertyW(set, ref data, property, out _, null, 0, out var size);
        if (size == 0) return "";
        var buffer = new byte[size];
        if (!SetupDiGetDeviceRegistryPropertyW(set, ref data, property, out _, buffer, size, out _)) return "";
        // REG_SZ or REG_MULTI_SZ: take the first string.
        var text = System.Text.Encoding.Unicode.GetString(buffer);
        var nul = text.IndexOf('\0');
        return nul >= 0 ? text[..nul] : text;
    }

    private static string? ReadPortName(IntPtr set, ref SpDevinfoData data)
    {
        var hkey = SetupDiOpenDevRegKey(set, ref data, DicsFlagGlobal, 0, DiregDev, KeyRead);
        if (hkey == IntPtr.Zero || hkey == new IntPtr(-1)) return null;
        using var key = RegistryKey.FromHandle(new SafeRegistryHandle(hkey, ownsHandle: true));
        return key.GetValue("PortName") as string;
    }
}
