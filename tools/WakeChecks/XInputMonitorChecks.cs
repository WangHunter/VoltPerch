using System.Text.Json;
using BatteryDesk;
using BatteryDesk.Hardware;

internal static class XInputMonitorChecks
{
    internal static async Task<int> RunAsync(string testRoot)
    {
        var passed = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new Exception("FAIL: " + name);
            passed++; Console.WriteLine("PASS: " + name);
        }
        async Task Until(Func<bool> condition)
        {
            using var timeout = new CancellationTokenSource(6500);
            while (!condition()) await Task.Delay(20, timeout.Token);
        }
        DeviceObservation Controller() => new("xinput:0", "Test controller", DeviceKind.Controller,
            "Test wireless", LinkState.Connected, null, false, "Test coarse reading", "Test XInput",
            CurrentBatteryLevel: BatteryLevel.Medium);
        DeviceObservation Percent(string id, int value) => new(id, "Test " + id, DeviceKind.Keyboard,
            "Test transport", LinkState.Connected, value, false, "Test percent", "Test provider");
        TargetHidProvider.Read = () => Task.FromResult<IReadOnlyList<DeviceObservation>>([Percent("vgn-v98pro-v2", 79)]);
        BluetoothProvider.Query = () => [Percent("bluetooth:test", 55)];
        HidNative.Enumeration = () => [];
        var connected = false;
        XInputProvider.ConnectionSignature = () => connected ? "1000" : "0000";
        XInputProvider.Query = _ => connected ? [Controller()] : [];
        var directory = Path.Combine(testRoot, "xinput-" + Guid.NewGuid().ToString("N"));

        // A controller powering on or off changes XInput slots without changing USB/HID topology.
        using (var monitor = new DeviceMonitor(directory))
        {
            monitor.Settings.RefreshSeconds = 300;
            await monitor.RefreshAsync();
            monitor.Start();
            connected = true;
            await Until(() => monitor.Devices.Any(d => d.Id == "xinput:0" && d.IsLive));
            var current = monitor.Devices.Single(d => d.Id == "xinput:0");
            Check(current.BatteryLevel == BatteryLevel.Medium && current.BatteryPercent == null,
                "XInput power-on triggers a read before the 5-minute battery interval and keeps the native level");
            await Until(() => !monitor.IsRefreshing);
            var saved = JsonSerializer.Deserialize<DeviceSnapshot[]>(File.ReadAllText(Path.Combine(directory, "devices.json")))!.Single(d => d.Id == "xinput:0");
            Check(saved.BatteryLevel == BatteryLevel.Medium && saved.BatteryPercent == null,
                "coarse controller level persists without an invented percentage");
            connected = false;
            await Until(() => monitor.Devices.Any(d => d.Id == "xinput:0" && d.Connection == LinkState.Disconnected));
            var offline = monitor.Devices.Single(d => d.Id == "xinput:0");
            Check(!offline.IsLive && offline.BatteryLevel == BatteryLevel.Medium && offline.LastSeenUtc == current.LastSeenUtc,
                "XInput power-off retains the last coarse level and original reading time");
            await Until(() => !monitor.IsRefreshing);
        }
        using (var monitor = new DeviceMonitor(directory))
        {
            var restored = monitor.Devices.Single(d => d.Id == "xinput:0");
            Check(restored.BatteryLevel == BatteryLevel.Medium && restored.BatteryPercent == null
                && !restored.IsLive && restored.Connection == LinkState.Unknown,
                "restart restores the controller level as non-live cache");

            // Both signature and read failures are isolated from the keyboard and Bluetooth provider.
            XInputProvider.ConnectionSignature = () => throw new IOException("Test signature unavailable");
            XInputProvider.Query = _ => throw new IOException("Test XInput unavailable");
            await monitor.RefreshAsync();
            var failed = monitor.Devices.Single(d => d.Id == "xinput:0");
            Check(failed.Connection == LinkState.Unknown && !failed.IsLive && failed.BatteryLevel == BatteryLevel.Medium,
                "XInput query failure preserves its level cache with unknown connection");
            Check(monitor.Devices.Single(d => d.Id == "vgn-v98pro-v2") is { IsLive: true, BatteryPercent: 79 }
                && monitor.Devices.Single(d => d.Id == "bluetooth:test") is { IsLive: true, BatteryPercent: 55 },
                "XInput failures do not invalidate fresh HID or Bluetooth readings");
        }

        // The controller connects after the query sampled no devices but before refresh finishes.
        // A post-query signature would swallow this change until the normal interval elapsed.
        connected = false;
        XInputProvider.ConnectionSignature = () => connected ? "1000" : "0000";
        XInputProvider.Query = _ => { if (!connected) { connected = true; return []; } return [Controller()]; };
        using (var monitor = new DeviceMonitor(Path.Combine(testRoot, "xinput-race-" + Guid.NewGuid().ToString("N"))))
        {
            monitor.Settings.RefreshSeconds = 300;
            await monitor.RefreshAsync();
            Check(monitor.Devices.All(d => d.Id != "xinput:0"), "race setup finishes the earlier disconnected observation");
            monitor.Start();
            await Until(() => monitor.Devices.Any(d => d.Id == "xinput:0" && d.IsLive));
            Check(monitor.Devices.Single(d => d.Id == "xinput:0").BatteryLevel == BatteryLevel.Medium,
                "a connection change during a read is detected by the next topology scan");
            await Until(() => !monitor.IsRefreshing);
        }
        XInputProvider.ConnectionSignature = () => "0000";
        XInputProvider.Query = _ => [];
        BluetoothProvider.Query = () => [];
        TargetHidProvider.Read = () => Task.FromResult<IReadOnlyList<DeviceObservation>>([]);
        return passed;
    }
}
