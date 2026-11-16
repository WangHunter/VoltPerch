using System;
using System.Collections.Generic;
using System.Linq;

namespace BatteryDesk;

public static class StatePolicy
{
    public static IReadOnlyList<DeviceSnapshot> Merge(IEnumerable<DeviceSnapshot> previous,
        IEnumerable<DeviceObservation> observations, DateTimeOffset now)
    {
        var cache = previous.ToDictionary(d => d.Id);
        var seen = new HashSet<string>();
        foreach (var observation in observations)
        {
            seen.Add(observation.Id);
            cache.TryGetValue(observation.Id, out var old);
            var validPercent = observation.CurrentBattery is >= 0 and <= 100;
            var validLevel = observation.CurrentBatteryLevel is BatteryLevel level && Enum.IsDefined(level);
            var valid = observation.Connection == LinkState.Connected && (validPercent || validLevel);
            var nearbyAirPodsReading = observation.Connection == LinkState.Disconnected
                && observation.BatteryComponents is not null && validPercent;
            var acceptReading = valid || nearbyAirPodsReading;
            cache[observation.Id] = new(observation.Id, observation.Name, observation.Kind, observation.Transport,
                observation.Connection, acceptReading ? (validPercent ? observation.CurrentBattery : null) : old?.BatteryPercent,
                acceptReading ? observation.IsCharging : null, valid, acceptReading ? now : old?.LastSeenUtc,
                observation.Message, observation.Provider,
                acceptReading ? (validPercent ? null : observation.CurrentBatteryLevel) : old?.BatteryLevel,
                acceptReading ? validPercent && observation.CurrentBatteryIsEstimated : old?.BatteryIsEstimated ?? false,
                observation.BatteryComponents ?? old?.BatteryComponents);
        }
        foreach (var id in cache.Keys.Where(id => !seen.Contains(id)).ToArray())
            cache[id] = cache[id] with { Connection = LinkState.Disconnected, IsLive = false, IsCharging = null,
                Message = "Disconnected; last valid reading retained" };
        return cache.Values.OrderBy(d => d.Connection switch
            { LinkState.Connected => 0, LinkState.Unknown => 1, _ => 2 })
            .ThenBy(d => d.Kind switch
            { DeviceKind.Keyboard => 0, DeviceKind.Mouse => 1, DeviceKind.Controller => 2, DeviceKind.Headphones => 3, _ => 4 })
            .ThenBy(d => d.Name, StringComparer.CurrentCulture).ToArray();
    }

    public static DeviceSnapshot? TrayTarget(IReadOnlyList<DeviceSnapshot> devices, string selectedId)
    {
        if (!string.IsNullOrEmpty(selectedId)) return devices.FirstOrDefault(d => d.Id == selectedId);
        var current = devices.Where(d => d.IsLive && d.Connection == LinkState.Connected);
        return current.Where(d => d.BatteryPercent is >= 0 and <= 100)
            .OrderBy(d => d.BatteryIsEstimated).ThenBy(d => d.IsCharging == true ? 1 : 0)
            .ThenBy(d => d.BatteryPercent).ThenBy(d => d.Id).FirstOrDefault()
            ?? current.Where(d => d.BatteryLevel is BatteryLevel level && Enum.IsDefined(level))
                .OrderBy(d => d.BatteryLevel).ThenBy(d => d.Id).FirstOrDefault();
    }
}

public sealed class AlertMemory
{
    public int? LastPercent { get; set; }
    public DateTimeOffset? LastTime { get; set; }
    public bool ShouldNotify(DeviceSnapshot device, AppSettings settings, DateTimeOffset now)
    {
        if (!device.IsLive || device.Connection != LinkState.Connected || device.BatteryIsEstimated
            || device.BatteryPercent is not int value) return false;
        if (value >= settings.LowBatteryThreshold + 5) { LastPercent = null; LastTime = null; }
        if (!settings.EnableNotifications || device.IsCharging == true || value > settings.LowBatteryThreshold) return false;
        if (LastPercent.HasValue && (value > LastPercent.Value - 5 || now - LastTime.GetValueOrDefault(now) < TimeSpan.FromMinutes(30))) return false;
        LastPercent = value; LastTime = now;
        return true;
    }
}
