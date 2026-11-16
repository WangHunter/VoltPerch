using System;

namespace BatteryDesk;

public enum DeviceKind { Keyboard, Mouse, Headphones, Controller, Other }
public enum LinkState { Connected, Disconnected, Unknown }
public enum BatteryLevel { Empty, Low, Medium, Full }

public sealed record BatteryComponents(int? Left, int? Right, int? Case,
    bool LeftCharging, bool RightCharging, bool CaseCharging, bool SingleBattery = false,
    DateTimeOffset? SeenUtc = null);

public sealed record DeviceSnapshot(
    string Id, string Name, DeviceKind Kind, string Transport,
    LinkState Connection, int? BatteryPercent, bool? IsCharging,
    bool IsLive, DateTimeOffset? LastSeenUtc, string Message, string Provider,
    BatteryLevel? BatteryLevel = null, bool BatteryIsEstimated = false,
    BatteryComponents? BatteryComponents = null);

public sealed record DeviceObservation(string Id, string Name, DeviceKind Kind, string Transport,
    LinkState Connection, int? CurrentBattery, bool? IsCharging, string Message, string Provider,
    BatteryLevel? CurrentBatteryLevel = null, bool CurrentBatteryIsEstimated = false,
    BatteryComponents? BatteryComponents = null);

public sealed record RemovedDevice(string Id, string Name, DeviceKind Kind, string Transport);

public sealed class AppSettings
{
    public string Language { get; set; } = "en";
    public string Theme { get; set; } = "System";
    public int RefreshSeconds { get; set; } = 60;
    public int LowBatteryThreshold { get; set; } = 20;
    public bool EnableNotifications { get; set; } = true;
    public bool NotificationSound { get; set; }
    public bool StartWithWindows { get; set; }
    public bool CloseToTray { get; set; } = true;
    public string TrayMode { get; set; } = "Battery";
    public string SelectedDeviceId { get; set; } = "";
}
