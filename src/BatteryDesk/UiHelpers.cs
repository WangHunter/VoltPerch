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
        if (d.BatteryComponents != null)
        {
            if (!HasRecentAirPodsBroadcast(d) && !d.IsLive)
                return T(d.Connection == LinkState.Disconnected ? "Last AirPods broadcast · offline" : "Last AirPods broadcast · not live");
            if (!HasRecentAirPodsBroadcast(d)) return T("AirPods component values · last broadcast");
            if (!d.IsLive && d.Connection == LinkState.Disconnected)
                return T("Nearby AirPods broadcast · approx. 10% steps");
            return T("AirPods component broadcast · approx. 10% steps");
        }
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
    internal static bool HasRecentAirPodsBroadcast(DeviceSnapshot d) =>
        d.BatteryComponents?.SeenUtc is DateTimeOffset seen && DateTimeOffset.UtcNow - seen <= TimeSpan.FromSeconds(60);
    internal static bool HasSeparateAirPodsComponents(DeviceSnapshot d) =>
        d.BatteryComponents is { SingleBattery: false };
    internal static string BatteryComponentValueText(int? percent) => percent is int value ? "≈" + value + "%" : "—";
    internal static string BatteryComponentText(int? percent, bool charging, bool isFresh = true)
    {
        if (percent is not int) return "—";
        return BatteryComponentValueText(percent) + (isFresh ? " · " + T(charging ? "Charging" : "Not charging") : "");
    }
    internal static string BatteryAccessibilityText(DeviceSnapshot d, AppSettings settings)
    {
        if (d.BatteryComponents is not { SingleBattery: false } components)
            return BatteryText(d) + ", " + BatteryCaption(d, settings);
        var fresh = HasRecentAirPodsBroadcast(d);
        return string.Join("; ", new[]
        {
            T("Left earbud") + ": " + BatteryComponentText(components.Left, components.LeftCharging, fresh),
            T("Right earbud") + ": " + BatteryComponentText(components.Right, components.RightCharging, fresh),
            T("Charging case") + ": " + BatteryComponentText(components.Case, components.CaseCharging, fresh),
            BatteryCaption(d, settings)
        });
    }
    internal static UIElement AirPodsComponentPanel(DeviceSnapshot d, double width, double labelSize,
        double valueSize, double statusSize, double captionSize, AppSettings settings, bool compactCaption = false)
    {
        var components = d.BatteryComponents ?? throw new ArgumentException("AirPods battery components are required", nameof(d));
        var fresh = HasRecentAirPodsBroadcast(d);
        var row = new Grid { Width = width, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var _ in new[] { 0, 1, 2 }) row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        AddComponent(row, 0, "Left (short)", components.Left, components.LeftCharging, d, fresh, labelSize, valueSize, statusSize, settings);
        AddComponent(row, 1, "Right (short)", components.Right, components.RightCharging, d, fresh, labelSize, valueSize, statusSize, settings);
        AddComponent(row, 2, "Case (short)", components.Case, components.CaseCharging, d, fresh, labelSize, valueSize, statusSize, settings);
        var panel = new StackPanel { Width = width, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        panel.Children.Add(row);
        var captionText = compactCaption ? BatteryCompactCaption(d, settings) : BatteryCaption(d, settings);
        var caption = Text(captionText, captionSize, "MutedBrush");
        caption.TextAlignment = TextAlignment.Center;
        caption.TextWrapping = System.Windows.TextWrapping.NoWrap;
        caption.TextTrimming = TextTrimming.CharacterEllipsis;
        caption.ToolTip = BatteryCaption(d, settings);
        caption.Margin = new Thickness(0, 2, 0, 0);
        panel.Children.Add(caption);
        return panel;
    }
    private static void AddComponent(Grid row, int column, string labelKey, int? percent, bool charging,
        DeviceSnapshot device, bool fresh, double labelSize, double valueSize, double statusSize, AppSettings settings)
    {
        var cell = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var label = Text(T(labelKey), labelSize, "MutedBrush");
        label.TextAlignment = TextAlignment.Center;
        label.TextWrapping = System.Windows.TextWrapping.NoWrap;
        cell.Children.Add(label);
        var valueColor = !fresh ? "MutedBrush" : percent is int value && value <= settings.LowBatteryThreshold ? "LowBrush" : "TextBrush";
        var valueText = Text(BatteryComponentValueText(percent), valueSize, valueColor);
        valueText.FontWeight = FontWeights.SemiBold;
        valueText.TextAlignment = TextAlignment.Center;
        valueText.TextWrapping = System.Windows.TextWrapping.NoWrap;
        cell.Children.Add(valueText);
        if (fresh && percent.HasValue)
        {
            var status = Text(T(charging ? "Charging" : "Not charging"), statusSize, charging ? "GoodBrush" : "MutedBrush");
            status.TextAlignment = TextAlignment.Center;
            status.TextWrapping = System.Windows.TextWrapping.NoWrap;
            cell.Children.Add(status);
        }
        Grid.SetColumn(cell, column);
        row.Children.Add(cell);
    }
    private static string BatteryCompactCaption(DeviceSnapshot d, AppSettings settings)
    {
        if (d.BatteryComponents is null) return BatteryCaption(d, settings);
        var fresh = HasRecentAirPodsBroadcast(d);
        if (!fresh) return d.IsLive ? T("Components cached") : T("Not live");
        return !d.IsLive && d.Connection == LinkState.Disconnected
            ? T("Nearby · approx. 10%")
            : T("Approx. 10% steps");
    }
    internal static TextBlock Text(string text, double size = 14, string? brush = null)
    {
        var block = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap };
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
