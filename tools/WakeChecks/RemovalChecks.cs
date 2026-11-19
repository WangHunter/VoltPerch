using System.Text.Json;
using BatteryDesk;
using BatteryDesk.Hardware;

internal static class RemovalChecks
{
    internal static async Task<int> RunAsync(string testRoot)
    {
        var passed = 0;
        const string keyboardId = "vgn-v98pro-v2";
        const string bluetoothId = "bluetooth:112233445566";
        const string otherBluetoothId = "bluetooth:665544332211";
        void Check(bool condition, string name)
        {
            if (!condition) throw new Exception("FAIL: " + name);
            passed++; Console.WriteLine("PASS: " + name);
        }
        DeviceObservation Observation(string id, int? value) => new(id, "Test " + id, DeviceKind.Keyboard,
            "Test transport", value.HasValue ? LinkState.Connected : LinkState.Unknown, value, false, "Test", "Test");
        TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
        void Save<T>(string directory, string name, T value) => File.WriteAllText(Path.Combine(directory, name), JsonSerializer.Serialize(value));
        T Load<T>(string directory, string name) => JsonSerializer.Deserialize<T>(File.ReadAllText(Path.Combine(directory, name)))!;

        var directory = Path.Combine(testRoot, "removal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var identities = new EndpointIdentityMap();
        identities.Resolve(keyboardId, new HidEndpoint("test-path-a", 0x320f, 0x5088, "serial-a"));
        Save(directory, "identities.json", identities.Assignments);
        var calls = 0;
        TargetHidProvider.Read = () => { Interlocked.Increment(ref calls); return Task.FromResult<IReadOnlyList<DeviceObservation>>([Observation(keyboardId, 10)]); };
        BluetoothProvider.Query = () => [Observation(bluetoothId, 18), Observation(otherBluetoothId, 55)];

        using (var monitor = new DeviceMonitor(directory))
        {
            await monitor.RefreshAsync();
            monitor.Settings.SelectedDeviceId = keyboardId;
            Check(Load<Dictionary<string, AlertMemory>>(directory, "alerts.json").ContainsKey(keyboardId),
                "test setup has a cached reading and alert state to remove");
            var identityJson = File.ReadAllText(Path.Combine(directory, "identities.json"));
            await monitor.RemoveDeviceAsync(keyboardId);
            Check(monitor.Devices.All(d => d.Id != keyboardId) && monitor.RemovedDevices.Single().Id == keyboardId,
                "remove moves only the selected device into the removed list");
            Check(Load<DeviceSnapshot[]>(directory, "devices.json").All(d => d.Id != keyboardId)
                && !Load<Dictionary<string, AlertMemory>>(directory, "alerts.json").ContainsKey(keyboardId),
                "remove clears persisted battery snapshot and alert state");
            Check(monitor.Settings.SelectedDeviceId == "" && Load<AppSettings>(directory, "settings.json").SelectedDeviceId == "",
                "remove clears and saves the fixed tray target");
            Check(File.ReadAllText(Path.Combine(directory, "identities.json")) == identityJson,
                "remove retains endpoint identity assignments");
            var beforeActivity = calls;
            monitor.NotifyKeyboardActivity(); await Task.Delay(250);
            Check(calls == beforeActivity, "activity from a removed keyboard does not trigger a refresh");
            await monitor.RefreshAsync();
            Check(monitor.Devices.All(d => d.Id != keyboardId), "fresh scan cannot reinsert a removed device");
            await monitor.RemoveDeviceAsync(keyboardId);
            Check(monitor.RemovedDevices.Count == 1, "repeated removal is idempotent");

            // Simulate an interrupted older cache write: startup must trust the exclusion list.
            var stale = new DeviceSnapshot(keyboardId, "Test keyboard", DeviceKind.Keyboard, "Test transport",
                LinkState.Connected, 10, false, true, DateTimeOffset.UtcNow, "Old cache", "Test");
            Save(directory, "devices.json", monitor.Devices.Append(stale).ToArray());
            Save(directory, "settings.json", new AppSettings { SelectedDeviceId = keyboardId });
        }

        using (var monitor = new DeviceMonitor(directory))
        {
            Check(monitor.Devices.All(d => d.Id != keyboardId) && monitor.RemovedDevices.Any(d => d.Id == keyboardId)
                && monitor.Settings.SelectedDeviceId == "", "restart filters old cache and invalid fixed selection");
            await monitor.RefreshAsync();
            Check(monitor.Devices.All(d => d.Id != keyboardId), "removed device remains hidden after restart and scan");

            var entered = Gate(); var release = Gate();
            TargetHidProvider.Read = async () => { entered.TrySetResult(); await release.Task; return [Observation(keyboardId, 81)]; };
            var restore = monitor.RestoreDeviceAsync(keyboardId);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var pending = monitor.Devices.Single(d => d.Id == keyboardId);
            Check(pending.Connection == LinkState.Unknown && pending.BatteryPercent == null && pending.LastSeenUtc == null && !pending.IsLive,
                "restore starts unknown without reviving the deleted battery cache");
            release.SetResult(); await restore;
            var current = monitor.Devices.Single(d => d.Id == keyboardId);
            Check(current.IsLive && current.BatteryPercent == 81 && monitor.RemovedDevices.All(d => d.Id != keyboardId),
                "restore reads a new battery value and removes the exclusion");
            Check(Load<RemovedDevice[]>(directory, "removed-devices.json").Length == 0
                && Load<Dictionary<string, string>>(directory, "identities.json").Values.Contains(keyboardId),
                "restore persists visibility and retains the original identity");

            await monitor.RemoveDeviceAsync(bluetoothId);
            await monitor.RefreshAsync();
            Check(monitor.Devices.All(d => d.Id != bluetoothId), "Bluetooth scan also respects the exclusion list");
            BluetoothProvider.Query = () => throw new IOException("Test Bluetooth unavailable");
            await monitor.RefreshAsync();
            Check(monitor.Devices.All(d => d.Id != bluetoothId)
                && monitor.Devices.Single(d => d.Id == otherBluetoothId).Connection == LinkState.Unknown,
                "Bluetooth failure fallback keeps removed devices hidden and preserves other records");
        }

        BluetoothProvider.Query = () => [];
        TargetHidProvider.Read = () => Task.FromResult<IReadOnlyList<DeviceObservation>>([Observation(keyboardId, 79)]);
        using (var monitor = new DeviceMonitor(Path.Combine(testRoot, "removal-race-" + Guid.NewGuid().ToString("N"))))
        {
            await monitor.RefreshAsync();
            var entered = Gate(); var release = Gate();
            TargetHidProvider.Read = async () => { entered.TrySetResult(); await release.Task; return [Observation(keyboardId, 81)]; };
            var refresh = monitor.RefreshAsync(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var remove = monitor.RemoveDeviceAsync(keyboardId);
            Check(!remove.IsCompleted, "removal waits for an in-progress refresh");
            release.SetResult(); await refresh; await remove;
            Check(monitor.Devices.Count == 0 && monitor.RemovedDevices.Single().Id == keyboardId,
                "an in-flight reading cannot resurrect a device after removal");
        }
        var mergeDirectory = Path.Combine(testRoot, "merge-keyboard");
        Directory.CreateDirectory(mergeDirectory);
        var oldPort = new HidEndpoint("old-port", 0x320f, 0x5088);
        var newPort = new HidEndpoint("new-port", 0x320f, 0x5088);
        var otherKeyboard = new HidEndpoint("another-keyboard", 0x320f, 0x5088);
        var map = new EndpointIdentityMap();
        var oldId = map.Resolve(keyboardId, oldPort);
        var newId = map.Resolve(keyboardId, newPort);
        var otherId = map.Resolve(keyboardId, otherKeyboard);
        Save(mergeDirectory, "identities.json", map.Assignments);
        TargetHidProvider.Read = () => Task.FromResult<IReadOnlyList<DeviceObservation>>([Observation(oldId, 76)]);
        using (var monitor = new DeviceMonitor(mergeDirectory))
        {
            await monitor.RefreshAsync();
            monitor.Settings.SelectedDeviceId = oldId;
            TargetHidProvider.Read = () => Task.FromResult<IReadOnlyList<DeviceObservation>>([Observation(newId, 18), Observation(otherId, 55)]);
            await monitor.RefreshAsync();
            var beforeMerge = monitor.Devices.ToArray();
            Check(beforeMerge.Single(d => d.Id == oldId) is { Connection: LinkState.Disconnected, BatteryPercent: 76 }
                && beforeMerge.Single(d => d.Id == newId) is { IsLive: true, BatteryPercent: 18 },
                "new receiver path reproduces a live and a cached VGN row");
            Check(!DeviceMonitor.CanMergeKeyboard(beforeMerge.Single(d => d.Id == otherId), beforeMerge.Single(d => d.Id == newId)),
                "two connected same-model keyboards cannot be merged");
            await monitor.MergeKeyboardAsync(oldId, newId);
            Check(monitor.Devices.Count == 2 && monitor.Devices.Single(d => d.Id == newId) is { IsLive: true, BatteryPercent: 18 }
                && monitor.Devices.Single(d => d.Id == otherId).BatteryPercent == 55 && monitor.Settings.SelectedDeviceId == newId,
                "confirmed merge keeps the live reading, tray pin and independent keyboard");
            Check(!Load<Dictionary<string, AlertMemory>>(mergeDirectory, "alerts.json").ContainsKey(oldId),
                "merged alias no longer has a separate alert history");

            // Model a restart after writing the alias but before saving the cleaned cache and selection.
            Save(mergeDirectory, "devices.json", beforeMerge);
            Save(mergeDirectory, "settings.json", new AppSettings { SelectedDeviceId = oldId });
        }
        var mergedMap = new EndpointIdentityMap(Load<Dictionary<string, string>>(mergeDirectory, "identities.json"));
        Check(mergedMap.Resolve(keyboardId, oldPort) == newId && mergedMap.Resolve(keyboardId, newPort) == newId
            && mergedMap.Resolve(keyboardId, otherKeyboard) == otherId,
            "both known ports resolve to the same ID after restart, with other devices distinct");
        using (var monitor = new DeviceMonitor(mergeDirectory))
        {
            Check(monitor.Devices.Count == 2 && monitor.Devices.Single(d => d.Id == newId) is { IsLive: false, BatteryPercent: 18 }
                && monitor.Settings.SelectedDeviceId == newId, "restart reconciles old cache and pin without treating the saved reading as live");
            TargetHidProvider.Read = () => Task.FromResult<IReadOnlyList<DeviceObservation>>([Observation(mergedMap.Resolve(keyboardId, oldPort), 64)]);
            await monitor.RefreshAsync();
            Check(monitor.Devices.Count == 2 && monitor.Devices.Single(d => d.Id == newId) is { IsLive: true, BatteryPercent: 64 },
                "using the old port updates the merged row");
        }
        TargetHidProvider.Read = () => Task.FromResult<IReadOnlyList<DeviceObservation>>([]);
        return passed;
    }
}
