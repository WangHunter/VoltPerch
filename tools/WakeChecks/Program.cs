using BatteryDesk;
using BatteryDesk.Hardware;

var passed = 0;
var testRoot = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N"));
DeviceMonitor NewMonitor() => new(Path.Combine(testRoot, Guid.NewGuid().ToString("N")));
IReadOnlyList<DeviceObservation> Reading(int? value) => new[] {
    new DeviceObservation("vgn-v98pro-v2", "Test keyboard", DeviceKind.Keyboard, "Test transport",
        value.HasValue ? LinkState.Connected : LinkState.Unknown, value, false, "Test response", "Test provider") };
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
    passed++; Console.WriteLine("PASS: " + name);
}
async Task Until(Func<bool> condition)
{
    using var timeout = new CancellationTokenSource(3000);
    while (!condition()) await Task.Delay(10, timeout.Token);
}
TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

// The failed keyboard query has completed logically, but another provider still owns the refresh.
// A wake event during this interval must survive and repeated events must coalesce.
{
    var entered = Gate(); var release = Gate(); var calls = 0;
    TargetHidProvider.Read = async () => {
        if (Interlocked.Increment(ref calls) == 1) { entered.SetResult(); await release.Task; return Reading(null); }
        return Reading(79);
    };
    using var monitor = NewMonitor();
    var first = monitor.RefreshAsync(); await entered.Task;
    for (var i = 0; i < 100; i++) monitor.NotifyKeyboardActivity();
    await Task.Delay(250);
    Check(calls == 1, "wake query waits behind the current refresh");
    release.SetResult(); await first;
    await Until(() => monitor.Devices.Any(d => d.IsLive && d.BatteryPercent == 79));
    Check(calls == 2, "100 queued wake events become one successful follow-up query");
    for (var i = 0; i < 100; i++) monitor.NotifyKeyboardActivity();
    await Task.Delay(250);
    Check(calls == 2, "fresh successful reading suppresses additional activity queries");
}

// If the existing read succeeds, the queued wake work rechecks freshness and skips redundant I/O.
{
    var entered = Gate(); var release = Gate(); var calls = 0;
    TargetHidProvider.Read = async () => { Interlocked.Increment(ref calls); entered.TrySetResult(); await release.Task; return Reading(79); };
    using var monitor = NewMonitor();
    var first = monitor.RefreshAsync(); await entered.Task;
    monitor.NotifyKeyboardActivity(); await Task.Delay(250);
    release.SetResult(); await first; await Task.Delay(250);
    Check(calls == 1, "queued activity reuses a fresh result from the preceding refresh");
}

// Shutdown cancels a waiter without starting another hardware read.
{
    var entered = Gate(); var release = Gate(); var calls = 0;
    TargetHidProvider.Read = async () => { Interlocked.Increment(ref calls); entered.TrySetResult(); await release.Task; return Reading(null); };
    using var monitor = NewMonitor();
    var first = monitor.RefreshAsync(); await entered.Task;
    monitor.NotifyKeyboardActivity(); await Task.Delay(250);
    monitor.Dispose(); release.SetResult(); await first; await Task.Delay(250);
    Check(calls == 1, "shutdown cancels a queued wake refresh");
}

// An unresponsive receiver must not be polled by every held-key event, but may retry after 5 s.
{
    var calls = 0;
    TargetHidProvider.Read = () => { Interlocked.Increment(ref calls); return Task.FromResult(Reading(null)); };
    using var monitor = NewMonitor();
    monitor.NotifyKeyboardActivity(); await Until(() => monitor.Devices.Count == 1);
    await Task.Delay(100);
    for (var i = 0; i < 100; i++) monitor.NotifyKeyboardActivity();
    await Task.Delay(300);
    Check(calls == 1, "failed read has a held-key backoff");
    await Task.Delay(5100); monitor.NotifyKeyboardActivity(); await Until(() => calls == 2);
    await Until(() => !monitor.IsRefreshing);
    Check(calls == 2, "activity can retry after the 5-second backoff");
}

{
    var calls = 0;
    TargetHidProvider.Read = () => { Interlocked.Increment(ref calls); return Task.FromResult(Reading(null)); };
    using var monitor = NewMonitor();
    monitor.NotifyKeyboardActivity(); monitor.Dispose(); await Task.Delay(300);
    Check(calls == 0, "shutdown cancels the initial wake debounce");
}
// A removed legacy keyboard must not suppress wake reads for another VGN ID.
{
    const string otherId = "vgn-v98pro-v2-84AD84AD84AD";
    TargetHidProvider.Read = () => Task.FromResult(Reading(76));
    using var monitor = NewMonitor();
    await monitor.RefreshAsync();
    await monitor.RemoveDeviceAsync("vgn-v98pro-v2");
    TargetHidProvider.Read = () => Task.FromResult<IReadOnlyList<DeviceObservation>>([Reading(null).Single() with { Id = otherId }]);
    await monitor.RefreshAsync();
    TargetHidProvider.Read = () => Task.FromResult<IReadOnlyList<DeviceObservation>>([Reading(61).Single() with { Id = otherId }]);
    monitor.NotifyKeyboardActivity();
    await Until(() => monitor.Devices.Single() is { IsLive: true, BatteryPercent: 61 });
    Check(monitor.Devices.Single().Id == otherId, "wake refresh reads a suffixed VGN identity even when the legacy device was removed");
}
// An explicit read made during another refresh waits for a new sample.
{
    var entered = Gate(); var release = Gate(); var calls = 0;
    TargetHidProvider.Read = async () => {
        if (Interlocked.Increment(ref calls) != 1) return Reading(61);
        entered.SetResult(); await release.Task; return Reading(76);
    };
    using var monitor = NewMonitor();
    var first = monitor.RefreshAsync();
    await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
    var manual = monitor.RefreshAsync();
    var waited = await Task.WhenAny(manual, Task.Delay(150)) != manual;
    release.SetResult();
    await Task.WhenAll(first, manual).WaitAsync(TimeSpan.FromSeconds(3));
    Check(waited && calls == 2 && monitor.Devices.Single().BatteryPercent == 61,
        "explicit refresh queued during a read takes a fresh sample");
}
// An alert file write failure must not hide fresh readings or consume an undelivered alert.
{
    TargetHidProvider.Read = () => Task.FromResult(Reading(10));
    using var monitor = NewMonitor();
    var blockedPath = Path.Combine(monitor.DataDirectory, "alerts.json.tmp");
    Directory.CreateDirectory(blockedPath);
    var alerts = 0;
    monitor.LowBattery += (_, devices) => alerts += devices.Count;
    await monitor.RefreshAsync();
    Check(monitor.Devices.Single() is { IsLive: true, BatteryPercent: 10 } && alerts == 1
        && monitor.StatusText.Contains("Could not save refreshed device data", StringComparison.Ordinal),
        "alert persistence failure preserves a live reading and delivers the notification");
    Directory.Delete(blockedPath);
    await monitor.RefreshAsync();
    Check(alerts == 1 && File.Exists(Path.Combine(monitor.DataDirectory, "alerts.json")),
        "the next refresh saves alert history without sending a duplicate notification");
}
passed += await RemovalChecks.RunAsync(testRoot);
passed += await XInputMonitorChecks.RunAsync(testRoot);
passed += await WgiMonitorChecks.RunAsync(testRoot);
Console.WriteLine($"{passed}/{passed} monitor checks passed; no hardware I/O.");
