using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace BatteryDesk.Hardware;

/// <summary>Reads Windows' classic Bluetooth device records without discovery or pairing.</summary>
public static class BluetoothProvider
{
    // This driver property is used by Bluetooth battery implementations such as
    // BatteryMonitor. It is not the similarly named Shell PKEY_Devices_BatteryLife.
    private static readonly PropertyKey BatteryKey = new(new Guid("104EA319-6EE2-4701-BD47-8DDBF425BBE5"), 2);
    // DEVPKEY_Bluetooth_DeviceAddress: a null-terminated UTF-16 string of 12 hex digits.
    private static readonly PropertyKey AddressKey = new(new Guid("2BD67D8B-8BEB-48D5-87E0-6CDA3428040A"), 1);

    public static IReadOnlyList<DeviceObservation> Read()
    {
        var devices = new Dictionary<ulong, BluetoothInfo>();
        var search = new SearchParameters
        {
            Size = Marshal.SizeOf<SearchParameters>(),
            ReturnAuthenticated = 1,
            ReturnRemembered = 1,
            ReturnConnected = 1,
            // No inquiry: reading this provider never discovers, pairs or connects devices.
            IssueInquiry = 0
        };
        var info = NewBluetoothInfo();
        var find = BluetoothFindFirstDevice(ref search, ref info);
        if (find == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            if (error is 0 or 259 or 1168) return Array.Empty<DeviceObservation>();
            throw new Win32Exception(error, "Could not read the Windows Bluetooth device list");
        }
        try
        {
            do
            {
                // The same remote device can be known to more than one local radio.
                if (info.Address != 0 && (info.Authenticated != 0 || info.Remembered != 0 || info.Connected != 0))
                {
                    if (!devices.TryGetValue(info.Address, out var previous) || info.Connected != 0 || previous.Connected == 0)
                        devices[info.Address] = info;
                }
                info = NewBluetoothInfo();
            } while (BluetoothFindNextDevice(find, ref info));
            var error = Marshal.GetLastWin32Error();
            if (error is not (0 or 259)) throw new Win32Exception(error, "Windows Bluetooth device enumeration was interrupted");
        }
        finally { BluetoothFindDeviceClose(find); }

        var batteryValues = ReadSystemBatteries(devices);
        var result = new List<DeviceObservation>(devices.Count);
        foreach (var device in devices.Values)
        {
            var connected = device.Connected != 0;
            int? battery = null;
            var message = connected ? "Connected; Windows provides no readable battery value" : "Disconnected; keeping the last successful battery reading";
            if (connected && batteryValues.TryGetValue(device.Address, out var values))
            {
                if (values.Count == 1)
                {
                    foreach (var value in values) battery = value;
                    message = "Latest battery value reported by Windows; update timing is managed by the system";
                }
                else if (values.Count > 1)
                    message = "Connected; conflicting Windows battery properties were ignored";
            }
            var address = device.Address.ToString("X12", CultureInfo.InvariantCulture);
            var name = string.IsNullOrWhiteSpace(device.Name) ? "Bluetooth device · " + address[^4..] : device.Name.Trim();
            result.Add(new DeviceObservation("bluetooth:" + address, name, GetKind(device.ClassOfDevice), "Bluetooth",
                connected ? LinkState.Connected : LinkState.Disconnected, battery, null, message,
                battery.HasValue ? "Windows Bluetooth / System battery property" : "Windows Classic Bluetooth"));
        }
        return result;
    }

    private static Dictionary<ulong, HashSet<int>> ReadSystemBatteries(Dictionary<ulong, BluetoothInfo> devices)
    {
        var values = new Dictionary<ulong, HashSet<int>>();
        var connected = new HashSet<ulong>();
        foreach (var device in devices.Values)
            if (device.Connected != 0) connected.Add(device.Address);
        if (connected.Count == 0) return values;

        // PRESENT identifies installed nodes, not an active Bluetooth link. The
        // independent fConnected check below is mandatory before returning a reading.
        var set = SetupDiGetClassDevsW(IntPtr.Zero, "BTHENUM", IntPtr.Zero, 0x6);
        if (set == new IntPtr(-1)) return values;
        try
        {
            for (uint index = 0; ; index++)
            {
                var data = new DeviceInfoData { Size = Marshal.SizeOf<DeviceInfoData>() };
                if (!SetupDiEnumDeviceInfo(set, index, ref data)) break;
                // HFP service nodes can be siblings of DEV_<address>, so their
                // own address property takes precedence over the ancestor fallback.
                var address = ReadAddressProperty(set, ref data) ?? GetRemoteAddress(data.DevInst);
                if (!address.HasValue || !connected.Contains(address.Value)) continue;
                var key = BatteryKey;
                var bytes = new byte[8];
                if (!SetupDiGetDevicePropertyW(set, ref data, ref key, out var type, bytes, bytes.Length, out var length, 0)) continue;
                AddBatteryValue(values, connected, address, ParseBattery(type, bytes, length));
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return values;
    }

    private static ulong? ReadAddressProperty(IntPtr set, ref DeviceInfoData data)
    {
        var key = AddressKey;
        var bytes = new byte[26];
        return SetupDiGetDevicePropertyW(set, ref data, ref key, out var type, bytes, bytes.Length, out var length, 0)
            ? ParseAddressProperty(type, bytes, length) : null;
    }

    internal static ulong? ParseAddressProperty(uint type, byte[] bytes, int length)
    {
        if (type != 18 || length != 26 || bytes.Length < length || bytes[24] != 0 || bytes[25] != 0) return null;
        return ParseAddressString(Encoding.Unicode.GetString(bytes, 0, 24));
    }

    internal static ulong? ParseAddressString(string value)
    {
        if (value.Length != 12) return null;
        foreach (var character in value)
            if (!char.IsAsciiHexDigit(character)) return null;
        return ulong.TryParse(value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var address)
            && address != 0 ? address : null;
    }

    internal static void AddBatteryValue(Dictionary<ulong, HashSet<int>> values, IReadOnlySet<ulong> connected,
        ulong? address, int? battery)
    {
        if (!address.HasValue || !connected.Contains(address.Value) || battery is not (>= 0 and <= 100)) return;
        if (!values.TryGetValue(address.Value, out var found)) values[address.Value] = found = new HashSet<int>();
        found.Add(battery.Value);
    }

    private static ulong? GetRemoteAddress(uint node)
    {
        // Match the remote-address ancestor, never device names: identical names
        // must remain separate and service nodes must not become duplicate devices.
        for (var depth = 0; depth < 8; depth++)
        {
            var id = new StringBuilder(512);
            if (CM_Get_Device_IDW(node, id, id.Capacity, 0) != 0) return null;
            var address = ParseAddress(id.ToString());
            if (address.HasValue) return address;
            if (CM_Get_Parent(out var parent, node, 0) != 0) return null;
            node = parent;
        }
        return null;
    }

    internal static ulong? ParseAddress(string id)
    {
        const string prefix = "BTHENUM\\DEV_";
        if (!id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || id.Length < prefix.Length + 12) return null;
        var end = prefix.Length + 12;
        if (id.Length > end && id[end] != '\\') return null;
        return ParseAddressString(id.Substring(prefix.Length, 12));
    }

    internal static int? ParseBattery(uint type, byte[] bytes, int length)
    {
        if (length < 0 || length > bytes.Length) return null;
        long value = type switch
        {
            3 when length >= 1 => bytes[0], // DEVPROP_TYPE_BYTE
            4 when length >= 2 => BitConverter.ToInt16(bytes, 0),
            5 when length >= 2 => BitConverter.ToUInt16(bytes, 0),
            6 when length >= 4 => BitConverter.ToInt32(bytes, 0),
            7 when length >= 4 => BitConverter.ToUInt32(bytes, 0),
            _ => -1
        };
        return value is >= 0 and <= 100 ? (int)value : null;
    }

    private static DeviceKind GetKind(uint classOfDevice)
    {
        var major = (classOfDevice >> 8) & 0x1F;
        if (major == 4) return DeviceKind.Headphones;
        if (major != 5) return DeviceKind.Other;
        if ((classOfDevice & 0x40) != 0) return DeviceKind.Keyboard;
        if ((classOfDevice & 0x80) != 0) return DeviceKind.Mouse;
        return ((classOfDevice >> 2) & 0xF) is 1 or 2 ? DeviceKind.Controller : DeviceKind.Other;
    }

    private static BluetoothInfo NewBluetoothInfo() => new() { Size = Marshal.SizeOf<BluetoothInfo>(), Name = "" };

    [StructLayout(LayoutKind.Sequential)]
    private struct SearchParameters
    {
        public int Size, ReturnAuthenticated, ReturnRemembered, ReturnUnknown, ReturnConnected, IssueInquiry;
        public byte TimeoutMultiplier;
        public IntPtr Radio;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct BluetoothInfo
    {
        public int Size;
        public ulong Address;
        public uint ClassOfDevice;
        public int Connected, Remembered, Authenticated;
        public SystemTime LastSeen, LastUsed;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)] public string Name;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemTime { public ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInfoData { public int Size; public Guid ClassGuid; public uint DevInst; public IntPtr Reserved; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid Format;
        public uint Id;
        public PropertyKey(Guid format, uint id) { Format = format; Id = id; }
    }

    [DllImport("bthprops.cpl", SetLastError = true, ExactSpelling = true)]
    private static extern IntPtr BluetoothFindFirstDevice(ref SearchParameters search, ref BluetoothInfo info);
    [DllImport("bthprops.cpl", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BluetoothFindNextDevice(IntPtr find, ref BluetoothInfo info);
    [DllImport("bthprops.cpl", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BluetoothFindDeviceClose(IntPtr find);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr SetupDiGetClassDevsW(IntPtr classGuid, string enumerator, IntPtr parent, uint flags);
    [DllImport("setupapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref DeviceInfoData data);
    [DllImport("setupapi.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDevicePropertyW(IntPtr set, ref DeviceInfoData data, ref PropertyKey key,
        out uint type, [Out] byte[] buffer, int size, out int required, uint flags);
    [DllImport("setupapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint CM_Get_Device_IDW(uint devInst, StringBuilder buffer, int length, uint flags);
    [DllImport("cfgmgr32.dll", ExactSpelling = true)]
    private static extern uint CM_Get_Parent(out uint parent, uint devInst, uint flags);
}
