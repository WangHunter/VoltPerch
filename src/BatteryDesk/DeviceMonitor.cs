using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BatteryDesk.Hardware;
using Microsoft.Win32;

namespace BatteryDesk;

public sealed class DeviceMonitor : IDisposable
{
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly object _fileLock = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<string, AlertMemory> _alerts;
    private EndpointIdentityMap _identities;
    private Task? _loop;
    private int _keyboardRefreshPending;
    private long _nextKeyboardRefreshTick;
    private DateTimeOffset _lastRefresh = DateTimeOffset.MinValue;
    private string _lastHidSignature = "";
    private string? _lastXInputSignature = "";
    private string? _lastWgiSignature = "";
    private string? _lastAirPodsSignature;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public IReadOnlyList<DeviceSnapshot> Devices { get; private set; }
    public IReadOnlyList<RemovedDevice> RemovedDevices { get; private set; }
    public AppSettings Settings { get; }
    public string DataDirectory { get; }
    public bool IsRefreshing { get; private set; }
    public string StatusText { get; private set; } = "Checking device connections...";
    public event EventHandler? Updated;
    public event EventHandler<IReadOnlyList<DeviceSnapshot>>? LowBattery;

    public DeviceMonitor(string? dataDirectory = null)
    {
        DataDirectory = dataDirectory ?? Path.Combine(AppContext.BaseDirectory, "data");
        Directory.CreateDirectory(DataDirectory);
        Settings = Read<AppSettings>("settings.json") ?? new();
        NormalizeSettings();
        _identities = new EndpointIdentityMap(Read<Dictionary<string, string>>("identities.json"));
        RemovedDevices = (Read<List<RemovedDevice>>("removed-devices.json") ?? new()).Where(d => !string.IsNullOrEmpty(d.Id)).ToArray();
        Devices = (Read<List<DeviceSnapshot>>("devices.json") ?? new()).Where(d => !string.IsNullOrEmpty(d.Id)).DistinctBy(d => d.Id)
            .Select(d => d with { IsLive = false, Connection = LinkState.Unknown, IsCharging = null, Message = "Waiting for connection confirmation; saved reading is not live" }).ToArray();
        _alerts = Read<Dictionary<string, AlertMemory>>("alerts.json") ?? new();
        ApplyDeviceAliases();
    }

    public void Start()
    {
        AirPodsAdvertisementProvider.Start();
        _loop ??= Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    AirPodsAdvertisementProvider.Start();
                    var endpoints = HidNative.Enumerate();
                    var signature = HidSignature(endpoints);
                    var xInputSignature = ReadXInputSignature();
                    var wgiSignature = ReadWgiSignature();
                    var airPodsSignature = AirPodsAdvertisementProvider.Signature;
                    if (signature != _lastHidSignature || xInputSignature != _lastXInputSignature || wgiSignature != _lastWgiSignature
                        || airPodsSignature != _lastAirPodsSignature
                        || DateTimeOffset.UtcNow - _lastRefresh >= TimeSpan.FromSeconds(Settings.RefreshSeconds))
                        await RefreshCoreAsync(endpoints, sampledConnections: (signature, xInputSignature, wgiSignature, airPodsSignature)).ConfigureAwait(false);
                    await Task.Delay(3000, _stop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    StatusText = "Detection failed; try Refresh: " + ex.Message;
                    Updated?.Invoke(this, EventArgs.Empty);
                    await Task.Delay(5000, _stop.Token).ConfigureAwait(false);
                }
            }
        });
    }

    public Task RefreshAsync() => Task.Run(() => RefreshCoreAsync(null, waitForRefresh: true));

    public async Task RemoveDeviceAsync(string id)
    {
        await _refreshGate.WaitAsync(_stop.Token).ConfigureAwait(false);
        try
        {
            _stop.Token.ThrowIfCancellationRequested();
            var device = Devices.FirstOrDefault(d => d.Id == id);
            if (device == null) return;
            var removed = RemovedDevices.Where(d => d.Id != id)
                .Append(new RemovedDevice(device.Id, device.Name, device.Kind, device.Transport)).ToArray();
            // Persist the exclusion first: even an interrupted cache write cannot redisplay it.
            Write("removed-devices.json", removed);
            RemovedDevices = removed;
            Devices = Devices.Where(d => d.Id != id).ToArray();
            _alerts.Remove(id);
            if (Settings.SelectedDeviceId == id)
            {
                Settings.SelectedDeviceId = "";
                Write("settings.json", Settings);
            }
            Write("devices.json", Devices);
            Write("alerts.json", _alerts);
            StatusText = "Removed from VoltPerch: " + device.Name;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StatusText = "Could not fully save device removal: " + ex.Message;
            throw;
        }
        finally
        {
            _refreshGate.Release();
            Updated?.Invoke(this, EventArgs.Empty);
        }
    }

    public async Task RestoreDeviceAsync(string id)
    {
        var restored = false;
        await _refreshGate.WaitAsync(_stop.Token).ConfigureAwait(false);
        try
        {
            _stop.Token.ThrowIfCancellationRequested();
            var device = RemovedDevices.FirstOrDefault(d => d.Id == id);
            if (device == null) return;
            var removed = RemovedDevices.Where(d => d.Id != id).ToArray();
            var devices = Devices.Where(d => d.Id != id).Append(new DeviceSnapshot(device.Id, device.Name,
                device.Kind, device.Transport, LinkState.Unknown, null, null, false, null,
                "Waiting for a new battery reading", "")).ToArray();
            var alerts = new Dictionary<string, AlertMemory>(_alerts);
            alerts.Remove(id);
            // Clear any old snapshot before lifting the exclusion, including after a partial prior save.
            Write("devices.json", devices);
            Write("alerts.json", alerts);
            Write("removed-devices.json", removed);
            Devices = devices;
            RemovedDevices = removed;
            _alerts.Remove(id);
            StatusText = "Restored " + device.Name + "; reading battery again";
            restored = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StatusText = "Could not fully save device restoration: " + ex.Message;
            throw;
        }
        finally
        {
            _refreshGate.Release();
            Updated?.Invoke(this, EventArgs.Empty);
        }
        if (restored && !_stop.IsCancellationRequested) await RefreshAsync().ConfigureAwait(false);
    }

    internal static bool CanMergeKeyboard(DeviceSnapshot source, DeviceSnapshot target) =>
        source.Id != target.Id && EndpointIdentityMap.IsVgnKeyboard(source.Id) && EndpointIdentityMap.IsVgnKeyboard(target.Id)
        && source.Connection == LinkState.Disconnected && target.Connection == LinkState.Connected;

    public async Task MergeKeyboardAsync(string sourceId, string targetId)
    {
        await _refreshGate.WaitAsync(_stop.Token).ConfigureAwait(false);
        try
        {
            _stop.Token.ThrowIfCancellationRequested();
            var source = Devices.FirstOrDefault(d => d.Id == sourceId);
            var target = Devices.FirstOrDefault(d => d.Id == targetId);
            if (source == null || target == null || !CanMergeKeyboard(source, target))
                throw new InvalidOperationException("These keyboard entries are no longer eligible to merge. Refresh and try again.");

            // Save the alias first. Startup applies it even if writing the other files is interrupted.
            var identities = new EndpointIdentityMap(new Dictionary<string, string>(_identities.Assignments));
            identities.MergeVgn(sourceId, targetId);
            Write("identities.json", identities.Assignments);
            _identities = identities;
            ApplyDeviceAliases();
            Write("devices.json", Devices);
            Write("alerts.json", _alerts);
            Write("settings.json", Settings);
        }
        finally
        {
            _refreshGate.Release();
            Updated?.Invoke(this, EventArgs.Empty);
        }
    }

    private void ApplyDeviceAliases()
    {
        RemovedDevices = RemovedDevices.Select(d => d with { Id = _identities.CanonicalId(d.Id) }).DistinctBy(d => d.Id).ToArray();
        var removedIds = RemovedDevices.Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
        Devices = Devices.GroupBy(d => _identities.CanonicalId(d.Id)).Where(group => !removedIds.Contains(group.Key))
            .Select(group =>
            {
                var chosen = group.OrderByDescending(d => d.IsLive).ThenByDescending(d => d.LastSeenUtc)
                    .ThenByDescending(d => d.Id == group.Key).First();
                var target = group.FirstOrDefault(d => d.Id == group.Key);
                return chosen with { Id = group.Key, Name = target?.Name ?? chosen.Name };
            }).ToArray();
        foreach (var (id, memory) in _alerts.ToArray())
        {
            var canonical = _identities.CanonicalId(id);
            if (id == canonical) continue;
            if (!_alerts.TryGetValue(canonical, out var current) || memory.LastTime > current.LastTime || current.LastTime == null)
                _alerts[canonical] = memory;
            _alerts.Remove(id);
        }
        foreach (var id in removedIds) _alerts.Remove(id);
        var selected = _identities.CanonicalId(Settings.SelectedDeviceId);
        Settings.SelectedDeviceId = removedIds.Contains(selected) ? "" : selected;
    }

    public void NotifyKeyboardActivity()
    {
        if (_stop.IsCancellationRequested || !KeyboardNeedsRefresh()
            || Environment.TickCount64 < Interlocked.Read(ref _nextKeyboardRefreshTick)
            || Interlocked.CompareExchange(ref _keyboardRefreshPending, 1, 0) != 0) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(150, _stop.Token).ConfigureAwait(false);
                // Keep one wake request behind an in-progress refresh instead of dropping it.
                await RefreshCoreAsync(null, waitForKeyboard: true).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            finally
            {
                // Failed reads must not cause every repeated key event to query the receiver.
                Interlocked.Exchange(ref _nextKeyboardRefreshTick, Environment.TickCount64 + 5000);
                Volatile.Write(ref _keyboardRefreshPending, 0);
            }
        });
    }

    private bool KeyboardNeedsRefresh()
    {
        var keyboards = Devices.Where(d => EndpointIdentityMap.IsVgnKeyboard(d.Id)).ToArray();
        if (keyboards.Length == 0)
            return !RemovedDevices.Any(d => EndpointIdentityMap.IsVgnKeyboard(d.Id));
        var cutoff = DateTimeOffset.UtcNow.AddSeconds(-Settings.RefreshSeconds);
        return keyboards.Any(d => d.Connection != LinkState.Disconnected
            && (!d.IsLive || d.LastSeenUtc.GetValueOrDefault() <= cutoff));
    }

    private static string HidSignature(IReadOnlyList<HidEndpoint> endpoints) =>
        string.Join("|", endpoints.Where(d => d.VendorId is 0x1038 or 0x320f
            || d.Path.Contains("&ig_", StringComparison.OrdinalIgnoreCase)).Select(d => d.Path).Order());

    private static string? ReadXInputSignature()
    {
        try { return XInputProvider.GetConnectionSignature(); }
        catch { return null; } // XInput being unavailable must not block HID or Bluetooth scans.
    }

    private static string? ReadWgiSignature()
    {
        try { return WindowsGamingInputProvider.GetConnectionSignature(); }
        catch { return null; }
    }

    private async Task RefreshCoreAsync(IReadOnlyList<HidEndpoint>? endpoints, bool waitForKeyboard = false,
        (string Hid, string? XInput, string? Wgi, string? AirPods)? sampledConnections = null, bool waitForRefresh = false)
    {
        if (waitForKeyboard) await _refreshGate.WaitAsync(_stop.Token).ConfigureAwait(false);
        else if (waitForRefresh) await _refreshGate.WaitAsync().ConfigureAwait(false);
        else if (!await _refreshGate.WaitAsync(0).ConfigureAwait(false)) return;
        try
        {
            if (_stop.IsCancellationRequested || (waitForKeyboard && !KeyboardNeedsRefresh())) return;
            IsRefreshing = true;
            Updated?.Invoke(this, EventArgs.Empty);
            endpoints ??= HidNative.Enumerate();
            // Save this pre-read sample, not a second sample after the queries: a connection
            // change during a read must still trigger another refresh on the next scan.
            (string Hid, string? XInput, string? Wgi, string? AirPods) connections = sampledConnections
                ?? (HidSignature(endpoints), ReadXInputSignature(), ReadWgiSignature(), AirPodsAdvertisementProvider.Signature);
            var observations = new List<DeviceObservation>(await TargetHidProvider.ReadAsync(endpoints, _identities).ConfigureAwait(false));
            string? bluetoothWarning = null;
            try
            {
                observations.AddRange(AirPodsAdvertisementProvider.Apply(BluetoothProvider.Read()));
            }
            catch (Exception ex)
            {
                // Bluetooth being disabled/unavailable must not invalidate USB/HID readings.
                bluetoothWarning = "Bluetooth status unavailable";
                observations.AddRange(Devices.Where(d => d.Id.StartsWith("bluetooth:", StringComparison.Ordinal)).Select(d =>
                    new DeviceObservation(d.Id, d.Name, d.Kind, d.Transport, LinkState.Unknown, null, null,
                        "Bluetooth status unavailable: " + ex.Message, d.Provider)));
            }
            string? controllerWarning = null;
            Exception? wgiFailure = null;
            Exception? xInputFailure = null;
            IReadOnlyList<DeviceObservation> wgiObservations = Array.Empty<DeviceObservation>();
            try
            {
                wgiObservations = await WindowsGamingInputProvider.ReadAsync(endpoints, _identities).ConfigureAwait(false);
                observations.AddRange(wgiObservations);
            }
            catch (Exception ex)
            {
                wgiFailure = ex;
                controllerWarning = "Some controller readings are unavailable";
            }
            // WGI owns Microsoft controllers when available. Keep XInput for other vendors,
            // or as a fallback while WGI's asynchronous enumeration has not produced devices.
            var observedIds = observations.Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
            try
            {
                var xInputEndpoints = wgiObservations.Count > 0
                    ? endpoints.Where(d => d.VendorId != 0x045e).ToArray() : endpoints;
                // XInput slots are global, even when Microsoft HID endpoints are excluded.
                // A WGI controller can still own the only active slot.
                observations.AddRange(XInputProvider.Read(xInputEndpoints, allowSlotMatch: wgiObservations.Count == 0)
                    .Where(d => observedIds.Add(d.Id)));
            }
            catch (Exception ex)
            {
                xInputFailure = ex;
                controllerWarning = "Some controller readings are unavailable";
            }
            // A WGI device can retain its legacy xinput: ID. Never let an error placeholder
            // replace a fresh reading from the other provider, regardless of that ID's prefix.
            foreach (var device in Devices.Where(d => !observedIds.Contains(d.Id)))
            {
                var failure = wgiFailure != null && device.Provider.Contains("Windows Gaming Input", StringComparison.Ordinal)
                    ? wgiFailure : xInputFailure != null && device.Id.StartsWith("xinput:", StringComparison.Ordinal)
                        ? xInputFailure : null;
                if (failure != null)
                    observations.Add(new DeviceObservation(device.Id, device.Name, device.Kind, device.Transport,
                        LinkState.Unknown, null, null, "Controller status unavailable: " + failure.Message, device.Provider));
            }
            string? saveWarning = null;
            void Persist<T>(string name, T value)
            {
                try { Write(name, value); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    saveWarning ??= "Could not save refreshed device data: " + ex.Message;
                }
            }
            // WGI may add persistent aliases to an existing XInput ID during this refresh.
            Persist("identities.json", _identities.Assignments);
            var now = DateTimeOffset.UtcNow;
            var removedIds = RemovedDevices.Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
            observations.RemoveAll(d => removedIds.Contains(d.Id));
            Devices = StatePolicy.Merge(Devices, observations, now);
            _lastHidSignature = connections.Hid;
            _lastXInputSignature = connections.XInput;
            _lastWgiSignature = connections.Wgi;
            _lastAirPodsSignature = connections.AirPods;
            _lastRefresh = now;
            Persist("devices.json", Devices);
            var notifications = new List<DeviceSnapshot>();
            foreach (var device in Devices)
            {
                if (!_alerts.TryGetValue(device.Id, out var memory)) _alerts[device.Id] = memory = new();
                if (memory.ShouldNotify(device, Settings, now)) notifications.Add(device);
            }
            Persist("alerts.json", _alerts);
            StatusText = $"Updated {now.ToLocalTime():HH:mm:ss} · Auto refresh {Settings.RefreshSeconds}s"
                + (bluetoothWarning == null ? "" : " · " + bluetoothWarning)
                + (controllerWarning == null ? "" : " · " + controllerWarning)
                + (saveWarning == null ? "" : " · " + saveWarning);
            if (notifications.Count > 0) LowBattery?.Invoke(this, notifications);
        }
        catch (Exception ex)
        {
            // An overall failure must never leave old readings advertised as live.
            Devices = Devices.Select(d => d with { IsLive = false, IsCharging = null, Message = "Refresh failed; last battery reading retained" }).ToArray();
            StatusText = "Reading failed; try again: " + ex.Message;
        }
        finally
        {
            IsRefreshing = false;
            _refreshGate.Release();
            Updated?.Invoke(this, EventArgs.Empty);
        }
    }

    public void SaveSettings(bool updateAutostart = false)
    {
        try
        {
            NormalizeSettings();
            Write("settings.json", Settings);
            if (updateAutostart) UpdateAutostart();
        }
        catch (Exception ex) { StatusText = "Could not fully save settings: " + ex.Message; }
        Updated?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateAutostart()
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        var command = "\"" + Environment.ProcessPath + "\" --tray";
        if (Settings.StartWithWindows) key.SetValue("VoltPerch", command);
        else if (string.Equals(key.GetValue("VoltPerch") as string, command, StringComparison.OrdinalIgnoreCase))
            key.DeleteValue("VoltPerch", false);
        // Migrate only the legacy entry belonging to this installation.
        if (key.GetValue("BatteryDesk") is string legacy && legacy.Contains(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase))
            key.DeleteValue("BatteryDesk", false);
    }
    private void NormalizeSettings()
    {
        if (Settings.RefreshSeconds is not (30 or 60 or 300)) Settings.RefreshSeconds = 60;
        if (Settings.LowBatteryThreshold is not (10 or 20 or 30)) Settings.LowBatteryThreshold = 20;
        if (Settings.Language is not ("en" or "zh-CN")) Settings.Language = "en";
        if (Settings.Theme is not ("System" or "Light" or "Dark")) Settings.Theme = "System";
        // Keep the legacy preference readable, but tray rendering is now battery-only.
        Settings.TrayMode = "Battery";
    }
    private T? Read<T>(string name)
    {
        var path = Path.Combine(DataDirectory, name);
        try { return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) : default; }
        catch (Exception ex) when (ex is JsonException or IOException) { StatusText = "Saved data could not be read; devices will be detected again"; return default; }
    }
    private void Write<T>(string name, T value)
    {
        lock (_fileLock)
        {
            var path = Path.Combine(DataDirectory, name);
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(value, Json));
            File.Move(path + ".tmp", path, true);
        }
    }
    public void Dispose() { _stop.Cancel(); AirPodsAdvertisementProvider.Stop(); }
}
