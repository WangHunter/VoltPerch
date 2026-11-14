using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;

namespace BatteryDesk.Hardware;

/// <summary>Observes only a keyboard input header's device identity, never key codes or typed text.</summary>
public sealed class KeyboardActivity : IDisposable
{
    private readonly HwndSource _source;
    private readonly Action _wake;
    private long _lastEvent;
    public KeyboardActivity(Action wake)
    {
        _wake = wake;
        _source = new HwndSource(new HwndSourceParameters("BatteryDesk.DeviceActivity") { WindowStyle = 0, Width = 1, Height = 1 });
        _source.AddHook(Hook);
        RegisterRawInputDevices([new RawDevice { UsagePage = 1, Usage = 6, Flags = 0x100, Target = _source.Handle }], 1, (uint)Marshal.SizeOf<RawDevice>());
    }
    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wp, IntPtr lp, ref bool handled)
    {
        if (msg != 0xff || Environment.TickCount64 - _lastEvent < 1000) return IntPtr.Zero;
        var size = (uint)Marshal.SizeOf<Header>();
        // RID_HEADER deliberately excludes the keyboard payload.
        if (GetRawInputData(lp, 0x10000005, out var header, ref size, (uint)Marshal.SizeOf<Header>()) == uint.MaxValue) return IntPtr.Zero;
        uint chars = 0;
        GetRawInputDeviceInfo(header.Device, 0x20000007, null, ref chars);
        if (chars == 0 || chars > 4096) return IntPtr.Zero;
        var name = new StringBuilder((int)chars + 1);
        if (GetRawInputDeviceInfo(header.Device, 0x20000007, name, ref chars) == uint.MaxValue) return IntPtr.Zero;
        var value = name.ToString();
        if (value.Contains("vid_320f&pid_5088", StringComparison.OrdinalIgnoreCase)) { _lastEvent = Environment.TickCount64; _wake(); }
        return IntPtr.Zero;
    }
    public void Dispose()
    {
        RegisterRawInputDevices([new RawDevice { UsagePage = 1, Usage = 6, Flags = 1, Target = IntPtr.Zero }], 1, (uint)Marshal.SizeOf<RawDevice>());
        _source.Dispose();
    }
    [StructLayout(LayoutKind.Sequential)] private struct RawDevice { public ushort UsagePage, Usage; public uint Flags; public IntPtr Target; }
    [StructLayout(LayoutKind.Sequential)] private struct Header { public uint Type, Size; public IntPtr Device, Param; }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterRawInputDevices(RawDevice[] devices, uint count, uint size);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint GetRawInputData(IntPtr input, uint command, out Header data, ref uint size, uint headerSize);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetRawInputDeviceInfoW")] private static extern uint GetRawInputDeviceInfo(IntPtr device, uint command, StringBuilder? data, ref uint size);
}
