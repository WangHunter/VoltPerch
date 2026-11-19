using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using Forms = System.Windows.Forms;

namespace BatteryDesk;

public sealed class TrayIcon : IDisposable
{
    private readonly HwndSource _source;
    private IntPtr _icon;
    private readonly uint _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    private const int Callback = 0x8001;
    private NotifyData _data;
    private DeviceMonitor? _lastMonitor;
    public event Action? Clicked;
    public event Action? OpenRequested;
    public event Action? RefreshRequested;
    public event Action? SettingsRequested;
    public event Action? ExitRequested;
    public TrayIcon()
    {
        _source = new HwndSource(new HwndSourceParameters("BatteryDesk.Tray") { WindowStyle = 0, Width = 1, Height = 1 });
        _source.AddHook(Hook);
        _icon = DrawIcon(null, false, "Battery", false);
        _data = new NotifyData { Size = Marshal.SizeOf<NotifyData>(), Window = _source.Handle, Id = 1,
            Flags = 1 | 2, Callback = Callback, Icon = _icon, Tip = "", Info = "", InfoTitle = "" };
        Shell_NotifyIcon(0, ref _data);
    }
    public void Update(DeviceMonitor monitor)
    {
        _lastMonitor = monitor;
        var target = StatePolicy.TrayTarget(monitor.Devices, monitor.Settings.SelectedDeviceId);
        var valid = target?.IsLive == true && target.Connection == LinkState.Connected;
        var percent = valid ? target!.BatteryPercent : null;
        var level = valid ? target!.BatteryLevel : null;
        var low = target != null && UiHelpers.Low(target, monitor.Settings);
        var next = DrawIcon(percent, target?.IsCharging == true && valid, monitor.Settings.TrayMode, low, level, valid && target!.BatteryIsEstimated);
        // No device tooltip on hover. The full device list opens only on click.
        _data.Flags = 2 | 4; _data.Icon = next; _data.Tip = "";
        Shell_NotifyIcon(1, ref _data);
        DestroyIcon(_icon); _icon = next;
    }
    public void NotifyLowBattery(string text, bool sound)
    {
        _data.Flags = 0x10;
        _data.InfoTitle = "VoltPerch — " + Localizer.T("Low battery");
        _data.Info = text.Length > 255 ? text[..255] : text;
        _data.InfoFlags = 2u | (sound ? 0u : 0x10u); // NIIF_WARNING | NIIF_NOSOUND
        _data.Timeout = 10000;
        Shell_NotifyIcon(1, ref _data);
        _data.Info = "";
    }
    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg is 0x001A or 0x031A && _lastMonitor != null) Update(_lastMonitor);
        if ((uint)msg == _taskbarCreated) { _data.Flags = 1 | 2; Shell_NotifyIcon(0, ref _data); }
        if (msg != Callback) return IntPtr.Zero;
        handled = true;
        switch (lParam.ToInt32())
        {
            case 0x202: Clicked?.Invoke(); break;
            case 0x203: OpenRequested?.Invoke(); break;
            case 0x205:
                var menu = new Forms.ContextMenuStrip();
                menu.Items.Add(Localizer.T("Open window"), null, (_, _) => OpenRequested?.Invoke());
                menu.Items.Add(Localizer.T("Refresh now"), null, (_, _) => RefreshRequested?.Invoke());
                menu.Items.Add(Localizer.T("Settings"), null, (_, _) => SettingsRequested?.Invoke());
                menu.Items.Add(new Forms.ToolStripSeparator());
                var dispatcher = _source.Dispatcher;
                // WinForms still uses the menu handle after Closed fires during an item click.
                menu.Closed += (_, _) => dispatcher.BeginInvoke(new Action(menu.Dispose));
                menu.Items.Add(Localizer.T("Exit"), null, (_, _) =>
                    dispatcher.BeginInvoke(new Action(() => ExitRequested?.Invoke())));
                SetForegroundWindow(_source.Handle);
                menu.Show(Forms.Cursor.Position);
                break;
            case 0x405: OpenRequested?.Invoke(); break; // balloon click
        }
        return IntPtr.Zero;
    }
    private static IntPtr DrawIcon(int? percent, bool charging, string mode, bool low, BatteryLevel? level = null, bool estimated = false)
    {
        var lightTaskbar = false;
        try
        {
            using var personalize = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            lightTaskbar = personalize?.GetValue("SystemUsesLightTheme") is int value && value != 0;
        }
        catch (System.Security.SecurityException) { }
        catch (UnauthorizedAccessException) { }
        catch (System.IO.IOException) { }
        Color? highContrastForeground = Forms.SystemInformation.HighContrast ? SystemColors.WindowText : null;
        using var bitmap = RenderIconBitmap(32, percent, charging, mode, low, level, estimated,
            lightTaskbar, highContrastForeground);
        return bitmap.GetHicon();
    }
    private static Bitmap RenderIconBitmap(int size, int? percent, bool charging, string mode, bool low,
        BatteryLevel? level = null, bool estimated = false, bool lightTaskbar = false, Color? foregroundOverride = null)
    {
        var bitmap = new Bitmap(size, size);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);
        g.ScaleTransform(size / 32f, size / 32f);
        var unknown = !percent.HasValue && !level.HasValue;
        // Follow the Windows taskbar, independently of the app's light/dark setting.
        // Keep the canvas transparent so this reads like a native notification icon.
        var foreground = foregroundOverride ?? (charging
            ? lightTaskbar ? Color.FromArgb(8, 116, 67) : Color.FromArgb(127, 224, 176)
            : low ? lightTaskbar ? Color.FromArgb(150, 84, 0) : Color.FromArgb(255, 197, 88)
            : lightTaskbar ? Color.FromArgb(39, 48, 60) : Color.FromArgb(245, 247, 250));
        using var brush = new SolidBrush(foreground);
        {
            // About 14x9px at 16px, including the terminal: visibly horizontal.
            using var pen = new Pen(brush, 2.5f) { LineJoin = LineJoin.Round };
            using var body = RoundedIconShape(3, 8, 22, 16, 2.5f);
            g.DrawPath(pen, body);
            g.FillRectangle(brush, 27, 13, 3, 6);
            if (charging)
            {
                // A bolt describes charging without inventing a battery fill level.
                g.FillPolygon(brush, new[] { new PointF(17, 10.5f), new PointF(10, 17.5f),
                    new PointF(14, 17.5f), new PointF(12, 21.5f), new PointF(20, 14), new PointF(16, 14) });
            }
            else if (low)
            {
                g.FillRectangle(brush, 13, 11.5f, 3, 6);
                g.FillRectangle(brush, 13, 20, 3, 2);
            }
            else if (unknown) g.FillRectangle(brush, 9, 14.5f, 10, 3);
            else if (percent is > 0) g.FillRectangle(brush, 7, 12, 14 * Math.Min(100, percent.Value) / 100f, 8);
            else if (level.HasValue)
            {
                // XInput reports discrete levels, so use separate bars rather than a percentage.
                var bars = level.Value switch { BatteryLevel.Low => 1, BatteryLevel.Medium => 2, BatteryLevel.Full => 3, _ => 0 };
                for (var i = 0; i < bars; i++) g.FillRectangle(brush, 7 + i * 5, 12, 3, 8);
            }
        }
        // Estimates remain visibly distinct without adding a numeric percentage.
        if (estimated && !unknown)
        {
            using var marker = new Pen(brush, 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawBezier(marker, 11, 3.5f, 14, 0.5f, 18, 6.5f, 21, 3.5f);
        }
        return bitmap;
    }
    private static GraphicsPath RoundedIconShape(float x, float y, float width, float height, float radius)
    {
        var path = new GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(x, y, diameter, diameter, 180, 90);
        path.AddArc(x + width - diameter, y, diameter, diameter, 270, 90);
        path.AddArc(x + width - diameter, y + height - diameter, diameter, diameter, 0, 90);
        path.AddArc(x, y + height - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
    public void Dispose() { Shell_NotifyIcon(2, ref _data); DestroyIcon(_icon); _source.Dispose(); }
    public System.Windows.Rect? GetBounds()
    {
        var identity = new IconIdentity { Size = (uint)Marshal.SizeOf<IconIdentity>(), Window = _source.Handle, Id = 1 };
        return Shell_NotifyIconGetRect(ref identity, out var bounds) == 0
            ? new System.Windows.Rect(bounds.Left, bounds.Top, bounds.Right-bounds.Left, bounds.Bottom-bounds.Top) : null;
    }
    [StructLayout(LayoutKind.Sequential)] private struct IconIdentity { public uint Size; public IntPtr Window; public uint Id; public Guid Guid; }
    [StructLayout(LayoutKind.Sequential)] private struct IconRect { public int Left, Top, Right, Bottom; }
    [DllImport("shell32.dll")] private static extern int Shell_NotifyIconGetRect(ref IconIdentity identifier, out IconRect bounds);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct NotifyData
    {
        public int Size; public IntPtr Window; public uint Id, Flags, Callback; public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Timeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags; public Guid Guid; public IntPtr BalloonIcon;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIcon(uint action, ref NotifyData data);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string message);
}
