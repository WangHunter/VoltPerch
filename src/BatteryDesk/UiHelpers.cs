using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace BatteryDesk;

internal static class UiHelpers
{
    internal static string T(string key, params object[] arguments) => Localizer.T(key, arguments);
    internal static string DeviceName(DeviceSnapshot device) => DeviceName(device.Name, device.Id, device.Kind);
    internal static string DeviceName(string name, string id, DeviceKind kind)
    {
        // User-assigned names are retained in storage. In English UI, keep recognizable
        // model text and a stable suffix when a name contains untranslated characters.
        if (Localizer.Language == "zh-CN" || !name.Any(c => c is >= '\u4e00' and <= '\u9fff')) return name;
        var suffix = id.Length > 6 ? id[^6..].ToUpperInvariant() : id;
        if (kind == DeviceKind.Controller && name.Contains("Xbox", StringComparison.OrdinalIgnoreCase))
            return "Xbox Wireless Controller · " + suffix;
        var model = Regex.Matches(name, @"[A-Za-z][A-Za-z0-9 ._-]*")
            .Select(m => m.Value.Trim()).OrderByDescending(s => s.Length).FirstOrDefault();
        return (string.IsNullOrWhiteSpace(model) ? KindName(kind) : model) + " · " + suffix;
    }
    internal static Brush Brush(FrameworkElement host, string name) => (Brush)host.FindResource(name);
    internal static bool IsDark(AppSettings settings)
    {
        if (settings.Theme == "Dark") return true;
        if (settings.Theme == "Light") return false;
        try { using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"); return key?.GetValue("AppsUseLightTheme") is int value && value == 0; } catch { return false; }
    }
    internal static void Theme(Window window, AppSettings settings)
    {
        bool dark = IsDark(settings);
        string[] keys = { "PageBrush", "SurfaceBrush", "TextBrush", "MutedBrush", "LineBrush", "TileBrush", "HoverBrush", "AccentBrush", "GoodBrush", "LowBrush" };
        string[] light = { "#F3F5F8", "#FFFFFF", "#20232A", "#626B7A", "#E7EAF0", "#F0F1F4", "#EDF4FF", "#087AFE", "#249A4E", "#B96B00" };
        string[] night = { "#171B23", "#232933", "#F3F5F8", "#AEB7C7", "#353E4B", "#353C48", "#2D4058", "#4A9BFF", "#70D88D", "#FFBA55" };
        for (int i = 0; i < keys.Length; i++) window.Resources[keys[i]] = new SolidColorBrush((Color)ColorConverter.ConvertFromString((dark ? night : light)[i]));
        if (SystemParameters.HighContrast)
        {
            window.Resources["PageBrush"] = SystemColors.WindowBrush; window.Resources["SurfaceBrush"] = SystemColors.WindowBrush;
            window.Resources["TextBrush"] = SystemColors.WindowTextBrush; window.Resources["MutedBrush"] = SystemColors.WindowTextBrush;
            window.Resources["LineBrush"] = SystemColors.WindowTextBrush; window.Resources["TileBrush"] = SystemColors.WindowBrush;
            window.Resources["HoverBrush"] = SystemColors.HighlightBrush; window.Resources["AccentBrush"] = SystemColors.HighlightBrush;
            window.Resources["GoodBrush"] = SystemColors.WindowTextBrush; window.Resources["LowBrush"] = SystemColors.WindowTextBrush;
        }
    }
    internal static string KindName(DeviceKind kind) => T(kind switch { DeviceKind.Keyboard => "Keyboard", DeviceKind.Mouse => "Mouse", DeviceKind.Headphones => "Headphones", DeviceKind.Controller => "Controller", _ => "Device" });
    internal static string ConnectionText(DeviceSnapshot d) => T(d.Connection switch { LinkState.Connected => "Connected", LinkState.Disconnected => "Disconnected", _ => "Connection unconfirmed" });
    internal static bool Low(DeviceSnapshot d, AppSettings settings) => d.IsLive && d.IsCharging != true
        && (d.BatteryPercent.HasValue && d.BatteryPercent.Value <= settings.LowBatteryThreshold
            || d.BatteryLevel is BatteryLevel.Empty or BatteryLevel.Low);
    internal static string BatteryText(DeviceSnapshot d) => d.BatteryPercent is int value ? (d.BatteryIsEstimated ? "≈" : "") + value + "%" : d.BatteryLevel switch
    {
        BatteryLevel.Empty => T("Empty"), BatteryLevel.Low => T("Low"), BatteryLevel.Medium => T("Medium"), BatteryLevel.Full => T("Full"), _ => "—"
    };
    internal static string BatteryCaption(DeviceSnapshot d, AppSettings settings)
    {
        if ((d.BatteryPercent.HasValue || d.BatteryLevel.HasValue) && !d.IsLive) return T(d.BatteryIsEstimated ? "Last estimate · offline" : "Last reading · offline");
        if (d.BatteryPercent.HasValue && d.BatteryIsEstimated)
            return T(d.IsCharging == true ? "Estimate · charging" : Low(d, settings) ? "⚠ Low estimate" : "System estimate");
        if (!d.BatteryPercent.HasValue && d.BatteryLevel.HasValue)
            return T(Low(d, settings) ? "⚠ Low battery" : "Reported level");
        if (!d.BatteryPercent.HasValue)
        {
            if (d.Connection == LinkState.Connected && d.Message.Contains("wired power", StringComparison.OrdinalIgnoreCase)) return T("Wired power");
            if (d.Message.Contains("not supported", StringComparison.OrdinalIgnoreCase) || d.Message.Contains("unsupported", StringComparison.OrdinalIgnoreCase)) return T("Not supported yet");
            if (d.Message.Contains("reading", StringComparison.OrdinalIgnoreCase) && d.Message.Contains("progress", StringComparison.OrdinalIgnoreCase)) return T("Reading...");
            return T(d.Connection == LinkState.Disconnected ? "No saved reading" : "Battery unavailable");
        }
        if (d.IsCharging == true) return T("Charging");
        if (Low(d, settings)) return T("⚠ Low battery");
        return T(d.IsCharging == false ? "Not charging" : "Current battery");
    }
    internal static TextBlock Text(string text, double size = 14, string? brush = null)
    {
        var block = new TextBlock { Text = Localizer.Translate(text), FontSize = size, TextWrapping = TextWrapping.Wrap };
        if (brush != null) block.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return block;
    }
    internal static Border Icon(DeviceKind kind, double size = 56, string? deviceName = null)
    {
        var icon = new DeviceIcon { Kind = kind, Earbuds = kind == DeviceKind.Headphones && (deviceName?.Contains("pods",StringComparison.OrdinalIgnoreCase) == true || deviceName?.Contains("buds",StringComparison.OrdinalIgnoreCase) == true || deviceName?.Contains("TWS",StringComparison.OrdinalIgnoreCase) == true), Width = size * .58, Height = size * .58, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        icon.SetResourceReference(DeviceIcon.ForegroundProperty, "TextBrush");
        var tile = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(size * .24), Child = icon, VerticalAlignment = VerticalAlignment.Center };
        tile.SetResourceReference(Border.BackgroundProperty, "TileBrush");
        return tile;
    }
}
