using System.Text.Json;
using BatteryDesk;
using BatteryDesk.Hardware;

internal static class WgiMonitorChecks
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
        const string firstId = "xinput:test-legacy";
        string secondId = "";
        DeviceObservation Controller(string id, int? percent) => new(id, "Test " + id, DeviceKind.Controller,
            "Test wireless", LinkState.Connected, percent, false, "Test WGI capacity", "Windows Gaming Input test",
            CurrentBatteryIsEstimated: percent.HasValue);
        DeviceObservation Legacy(string id) => new(id, "Test fallback", DeviceKind.Controller,
            "Test wireless", LinkState.Connected, null, false, "Test XInput level", "Test XInput",
            CurrentBatteryLevel: BatteryLevel.Medium);
        DeviceObservation Peripheral(string id, int percent) => new(id, "Test " + id, DeviceKind.Keyboard,
            "Test transport", LinkState.Connected, percent, false, "Test percent", "Test provider");
        var endpoints = new HidEndpoint[] { new("test-ms&ig_00", 0x045e), new("test-other&ig_01", 0x1234) };
        HidNative.Enumeration = () => endpoints;
        TargetHidProvider.Read = () => Task.FromResult<IReadOnlyList<DeviceObservation>>([Peripheral("vgn-v98pro-v2", 79)]);
        BluetoothProvider.Query = () => [Peripheral("bluetooth:test", 55)];
        XInputProvider.ConnectionSignature = () => "1100";
        var excludedMicrosoft = false;
        XInputProvider.Query = candidates =>
        {
            excludedMicrosoft = candidates.Count == 1 && candidates[0].VendorId == 0x1234;
            return [Legacy(firstId), Legacy("xinput:other-vendor")];
        };
        WindowsGamingInputProvider.ConnectionSignature = () => "controller-one|controller-two";
        WindowsGamingInputProvider.Query = (_, identities) =>
        {
            var first = identities.ResolveWgiController("test-controller-one", firstId);
            secondId = identities.ResolveWgiController("test-controller-two");
            return Task.FromResult<IReadOnlyList<DeviceObservation>>([Controller(first, 71), Controller(secondId, 27)]);
        };
        var directory = Path.Combine(testRoot, "wgi-" + Guid.NewGuid().ToString("N"));
        DateTimeOffset? secondReadingTime;
        using (var monitor = new DeviceMonitor(directory))
        {
            await monitor.RefreshAsync();
            Check(excludedMicrosoft && monitor.Devices.Count(d => d.Id == firstId) == 1
                && monitor.Devices.Single(d => d.Id == firstId) is { IsLive: true, BatteryPercent: 71, BatteryIsEstimated: true }
                && monitor.Devices.Single(d => d.Id == "xinput:other-vendor").IsLive,
                "WGI takes over Microsoft controllers without duplicate legacy rows and keeps other vendors");
            var saved = JsonSerializer.Deserialize<DeviceSnapshot[]>(File.ReadAllText(Path.Combine(directory, "devices.json")))!;
            Check(saved.Single(d => d.Id == firstId).BatteryPercent == 71
                && saved.Single(d => d.Id == secondId) is { BatteryPercent: 27, BatteryIsEstimated: true },
                "two WGI controllers persist their own estimated readings");
            var aliases = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(directory, "identities.json")))!;
            Check(aliases["wgi|id:test-controller-one"] == firstId && aliases["wgi|id:test-controller-two"] == secondId,
                "identity aliases created during WGI reads are saved in the same refresh");
            secondReadingTime = monitor.Devices.Single(d => d.Id == secondId).LastSeenUtc;
            WindowsGamingInputProvider.Query = (_, _) => Task.FromResult<IReadOnlyList<DeviceObservation>>([Controller(firstId, 70)]);
            await monitor.RefreshAsync();
            Check(monitor.Devices.Single(d => d.Id == secondId) is { Connection: LinkState.Disconnected, IsLive: false, BatteryPercent: 27, BatteryIsEstimated: true }
                && monitor.Devices.Single(d => d.Id == secondId).LastSeenUtc == secondReadingTime,
                "disconnecting one WGI controller keeps its own cached reading and timestamp");
        }
        using (var monitor = new DeviceMonitor(directory))
        {
            Check(monitor.Devices.Single(d => d.Id == firstId) is { BatteryPercent: 70, BatteryIsEstimated: true, IsLive: false }
                && monitor.Devices.Single(d => d.Id == secondId) is { BatteryPercent: 27, BatteryIsEstimated: true, IsLive: false },
                "restart retains separate WGI readings and estimated markers as non-live cache");
            WindowsGamingInputProvider.ConnectionSignature = () => throw new IOException("Test WGI enumeration unavailable");
            WindowsGamingInputProvider.Query = (_, _) => throw new IOException("Test WGI unavailable");
            var fallbackSawMicrosoft = false;
            XInputProvider.Query = candidates =>
            {
                fallbackSawMicrosoft = candidates.Any(d => d.VendorId == 0x045e);
                return [Legacy(firstId)];
            };
            await monitor.RefreshAsync();
            Check(fallbackSawMicrosoft && monitor.Devices.Single(d => d.Id == firstId) is
                { IsLive: true, BatteryLevel: BatteryLevel.Medium, BatteryPercent: null, BatteryIsEstimated: false },
                "WGI failure falls back to XInput and cannot overwrite its fresh shared-ID observation");
            Check(monitor.Devices.Single(d => d.Id == secondId) is
                { Connection: LinkState.Unknown, IsLive: false, BatteryPercent: 27, BatteryIsEstimated: true }
                && monitor.Devices.Single(d => d.Id == "vgn-v98pro-v2") is { IsLive: true, BatteryPercent: 79 }
                && monitor.Devices.Single(d => d.Id == "bluetooth:test") is { IsLive: true, BatteryPercent: 55 },
                "WGI failure preserves unmatched controller cache without invalidating HID or Bluetooth");
            WindowsGamingInputProvider.ConnectionSignature = () => "controller-one|controller-two";
            WindowsGamingInputProvider.Query = (_, _) => Task.FromResult<IReadOnlyList<DeviceObservation>>(
                [Controller(firstId, 69), Controller(secondId, null)]);
            XInputProvider.Query = _ => throw new IOException("Test XInput unavailable");
            await monitor.RefreshAsync();
            Check(monitor.Devices.Single(d => d.Id == firstId) is { IsLive: true, BatteryPercent: 69, BatteryIsEstimated: true },
                "XInput failure cannot overwrite fresh WGI data using a legacy xinput ID");
            Check(monitor.Devices.Single(d => d.Id == secondId) is
                { Connection: LinkState.Connected, IsLive: false, BatteryPercent: 27, BatteryIsEstimated: true }
                && monitor.Devices.Single(d => d.Id == secondId).LastSeenUtc == secondReadingTime,
                "a single controller battery failure keeps its connection and last reading independently");
        }

        // WGI populates its device collection asynchronously. A device may appear during the
        // first read even when both USB topology and XInput slots remain unchanged.
        var enumerated = false;
        var injectEnumeration = true;
        WindowsGamingInputProvider.ConnectionSignature = () => enumerated ? "controller-one" : "";
        WindowsGamingInputProvider.Query = (_, _) =>
        {
            if (injectEnumeration) { injectEnumeration = false; enumerated = true; return Task.FromResult<IReadOnlyList<DeviceObservation>>([]); }
            return Task.FromResult<IReadOnlyList<DeviceObservation>>(enumerated ? [Controller(firstId, 68)] : []);
        };
        XInputProvider.ConnectionSignature = () => "1000";
        XInputProvider.Query = _ => [];
        using (var monitor = new DeviceMonitor(Path.Combine(testRoot, "wgi-race-" + Guid.NewGuid().ToString("N"))))
        {
            monitor.Settings.RefreshSeconds = 300;
            await monitor.RefreshAsync();
            Check(monitor.Devices.All(d => d.Id != firstId), "WGI race setup initially reports no enumerated controllers");
            monitor.Start();
            await Until(() => monitor.Devices.Any(d => d.Id == firstId && d.IsLive));
            Check(monitor.Devices.Single(d => d.Id == firstId).BatteryPercent == 68,
                "WGI enumeration changes during a read trigger a scan before the normal refresh interval");
            await Until(() => !monitor.IsRefreshing);
            enumerated = false;
            await Until(() => monitor.Devices.Any(d => d.Id == firstId && d.Connection == LinkState.Disconnected));
            Check(monitor.Devices.Single(d => d.Id == firstId) is { IsLive: false, BatteryPercent: 68, BatteryIsEstimated: true },
                "WGI signature-only disconnect automatically preserves cached estimated battery");
            await Until(() => !monitor.IsRefreshing);
        }
        WindowsGamingInputProvider.Query = (_, _) => Task.FromResult<IReadOnlyList<DeviceObservation>>([]);
        WindowsGamingInputProvider.ConnectionSignature = () => "";
        XInputProvider.Query = _ => [];
        XInputProvider.ConnectionSignature = () => "0000";
        BluetoothProvider.Query = () => [];
        TargetHidProvider.Read = () => Task.FromResult<IReadOnlyList<DeviceObservation>>([]);
        HidNative.Enumeration = () => [];
        return passed;
    }
}
