using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace BatteryDesk.Hardware;

public sealed record HidEndpoint(string Path, ushort VendorId, ushort ProductId,
    ushort UsagePage, ushort Usage, int InputLength, int OutputLength, int FeatureLength,
    string Product, string Serial);

public static class HidNative
{
    public static IReadOnlyList<HidEndpoint> Enumerate()
    {
        var result = new List<HidEndpoint>();
        HidD_GetHidGuid(out var guid);
        var set = SetupDiGetClassDevs(ref guid, null, IntPtr.Zero, 0x12);
        if (set == new IntPtr(-1)) return result;
        try
        {
            for (uint index = 0; ; index++)
            {
                var item = new InterfaceData { Size = Marshal.SizeOf<InterfaceData>() };
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, index, ref item)) break;
                SetupDiGetDeviceInterfaceDetail(set, ref item, IntPtr.Zero, 0, out var required, IntPtr.Zero);
                if (required < 8) continue;
                var buffer = Marshal.AllocHGlobal((int)required);
                try
                {
                    Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetail(set, ref item, buffer, required, out _, IntPtr.Zero)) continue;
                    var path = Marshal.PtrToStringUni(buffer + 4)!;
                    using var handle = CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                    if (handle.IsInvalid) continue;
                    var attributes = new Attributes { Size = Marshal.SizeOf<Attributes>() };
                    if (!HidD_GetAttributes(handle, ref attributes) || !HidD_GetPreparsedData(handle, out var pp)) continue;
                    try
                    {
                        if (HidP_GetCaps(pp, out var caps) < 0) continue;
                        result.Add(new(path, attributes.VendorId, attributes.ProductId,
                            caps.UsagePage, caps.Usage, caps.InputLength, caps.OutputLength, caps.FeatureLength,
                            GetString(handle, false), GetString(handle, true)));
                    }
                    finally { HidD_FreePreparsedData(pp); }
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return result;
    }

    public static async Task<byte[]> QueryAsync(HidEndpoint device, byte[] command,
        Func<byte[], bool>? accept = null, int timeoutMs = 1200)
    {
        if (device.OutputLength < command.Length || device.InputLength < 1) throw new IOException("Device report lengths do not match");
        using var handle = CreateFile(device.Path, 0xC0000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
        if (handle.IsInvalid) throw new IOException("Could not open the device read interface", Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));
        HidD_FlushQueue(handle);
        using var stream = new FileStream(handle, FileAccess.ReadWrite, 1, true);
        using var cts = new CancellationTokenSource(timeoutMs);
        var output = new byte[device.OutputLength];
        command.CopyTo(output, 0);
        await stream.WriteAsync(output.AsMemory(), cts.Token).ConfigureAwait(false);
        while (true)
        {
            var input = new byte[device.InputLength];
            var count = await stream.ReadAsync(input.AsMemory(), cts.Token).ConfigureAwait(false);
            if (count == 0) throw new IOException("Device returned no data");
            if (count != input.Length) Array.Resize(ref input, count);
            if (accept == null || accept(input)) return input;
        }
    }

    private static string GetString(SafeFileHandle handle, bool serial)
    {
        var bytes = new byte[256];
        var success = serial ? HidD_GetSerialNumberString(handle, bytes, bytes.Length) : HidD_GetProductString(handle, bytes, bytes.Length);
        return success ? Encoding.Unicode.GetString(bytes).TrimEnd('\0') : "";
    }
    [StructLayout(LayoutKind.Sequential)] private struct InterfaceData { public int Size; public Guid ClassGuid; public uint Flags; public IntPtr Reserved; }
    [StructLayout(LayoutKind.Sequential)] private struct Attributes { public int Size; public ushort VendorId, ProductId, Version; }
    [StructLayout(LayoutKind.Sequential)] private struct Caps
    {
        public ushort Usage, UsagePage, InputLength, OutputLength, FeatureLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort LinkNodes, InputButtons, InputValues, InputIndices, OutputButtons, OutputValues, OutputIndices, FeatureButtons, FeatureValues, FeatureIndices;
    }
    [DllImport("hid.dll")] private static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("hid.dll")] private static extern bool HidD_GetAttributes(SafeFileHandle handle, ref Attributes attributes);
    [DllImport("hid.dll")] private static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out IntPtr data);
    [DllImport("hid.dll")] private static extern bool HidD_FreePreparsedData(IntPtr data);
    [DllImport("hid.dll")] private static extern int HidP_GetCaps(IntPtr data, out Caps caps);
    [DllImport("hid.dll")] private static extern bool HidD_FlushQueue(SafeFileHandle handle);
    [DllImport("hid.dll")] private static extern bool HidD_GetProductString(SafeFileHandle handle, byte[] data, int length);
    [DllImport("hid.dll")] private static extern bool HidD_GetSerialNumberString(SafeFileHandle handle, byte[] data, int length);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")] private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetupDiGetClassDevsW")] private static extern IntPtr SetupDiGetClassDevs(ref Guid guid, string? enumerator, IntPtr parent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr info, ref Guid guid, uint index, ref InterfaceData data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetupDiGetDeviceInterfaceDetailW")] private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref InterfaceData data, IntPtr detail, uint length, out uint required, IntPtr info);
    [DllImport("setupapi.dll")] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
}
