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
    private readonly EndpointIdentityMap _identities;
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
        RemovedDevices = (Read<List<RemovedDevice>>("removed-devices.json") ?? new()).Where(d => !string.IsNullOrEmpty(d.Id)).DistinctBy(d => d.Id).ToArray();
        var removedIds = RemovedDevices.Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
        Devices = (Read<List<DeviceSnapshot>>("devices.json") ?? new()).Where(d => !string.IsNullOrEmpty(d.Id) && !removedIds.Contains(d.Id)).DistinctBy(d => d.Id)
            .Select(d => d with { IsLive = false, Connection = LinkState.Unknown, IsCharging = null, Message = "Waiting for connection confirmation; saved reading is not live" }).ToArray();
        _alerts = Read<Dictionary<string, AlertMemory>>("alerts.json") ?? new();
        foreach (var id in removedIds) _alerts.Remove(id);
        if (removedIds.Contains(Settings.SelectedDeviceId)) Settings.SelectedDeviceId = "";
        _identities = new EndpointIdentityMap(Read<Dictionary<string, string>>("identities.json"));
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

    public Task RefreshAsync() => Task.Run(() => RefreshCoreAsync(null));

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
        if (RemovedDevices.Any(d => d.Id == "vgn-v98pro-v2")) return false;
        var keyboard = Devices.FirstOrDefault(d => d.Id == "vgn-v98pro-v2");
        return keyboard?.IsLive != true
            || DateTimeOffset.UtcNow - keyboard.LastSeenUtc.GetValueOrDefault() >= TimeSpan.FromSeconds(Settings.RefreshSeconds);
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
        (string Hid, string? XInput, string? Wgi, string? AirPods)? sampledConnections = null)
    {
        if (waitForKeyboard) await _refreshGate.WaitAsync(_stop.Token).ConfigureAwait(false);
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
                observations.AddRange(XInputProvider.Read(xInputEndpoints).Where(d => observedIds.Add(d.Id)));
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
            // WGI may add persistent aliases to an existing XInput ID during this refresh.
            Write("identities.json", _identities.Assignments);
            var now = DateTimeOffset.UtcNow;
            var removedIds = RemovedDevices.Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
            observations.RemoveAll(d => removedIds.Contains(d.Id));
            Devices = StatePolicy.Merge(Devices, observations, now);
            _lastHidSignature = connections.Hid;
            _lastXInputSignature = connections.XInput;
            _lastWgiSignature = connections.Wgi;
            _lastAirPodsSignature = connections.AirPods;
            _lastRefresh = now;
            Write("devices.json", Devices);
            var notifications = new List<DeviceSnapshot>();
            foreach (var device in Devices)
            {
                if (!_alerts.TryGetValue(device.Id, out var memory)) _alerts[device.Id] = memory = new();
                if (memory.ShouldNotify(device, Settings, now)) notifications.Add(device);
            }
            Write("alerts.json", _alerts);
            StatusText = $"Updated {now.ToLocalTime():HH:mm:ss} · Auto refresh {Settings.RefreshSeconds}s"
                + (bluetoothWarning == null ? "" : " · " + bluetoothWarning)
                + (controllerWarning == null ? "" : " · " + controllerWarning);
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

    public void SaveSettings()
    {
        try
        {
            NormalizeSettings();
            Write("settings.json", Settings);
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (Settings.StartWithWindows) key.SetValue("VoltPerch", "\"" + Environment.ProcessPath + "\" --tray");
            else key.DeleteValue("VoltPerch", false);
            // Migrate only the legacy entry belonging to this installation.
            if (key.GetValue("BatteryDesk") is string legacy && legacy.Contains(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase))
                key.DeleteValue("BatteryDesk", false);
        }
        catch (Exception ex) { StatusText = "Could not fully save settings: " + ex.Message; }
        Updated?.Invoke(this, EventArgs.Empty);
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
