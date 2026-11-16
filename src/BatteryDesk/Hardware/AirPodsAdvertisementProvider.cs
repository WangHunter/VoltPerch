using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Storage.Streams;

namespace BatteryDesk.Hardware;

internal static class AirPodsAdvertisementProvider
{
    private static readonly object Gate = new();
    private static BluetoothLEAdvertisementWatcher? _watcher;
    private static AirPodsAdvertisementReading? _latest;
    private static string? _startError;
    private static bool _enabled;
    private static int _startPending;

    public static void Start()
    {
        lock (Gate) _enabled = true;
        if (Interlocked.CompareExchange(ref _startPending, 1, 0) != 0) return;
        _ = Task.Run(StartCore);
    }

    private static void StartCore()
    {
        BluetoothLEAdvertisementWatcher? watcher = null;
        try
        {
            lock (Gate)
            {
                if (!_enabled) return;
                if (_watcher?.Status is BluetoothLEAdvertisementWatcherStatus.Started
                    or BluetoothLEAdvertisementWatcherStatus.Stopping)
                {
                    _startError = null;
                    return;
                }
                if (_watcher == null)
                {
                    _watcher = new BluetoothLEAdvertisementWatcher
                    {
                        ScanningMode = BluetoothLEScanningMode.Passive
                    };
                    _watcher.Received += OnReceived;
                }
                watcher = _watcher;
            }

            var startingWatcher = watcher ?? throw new InvalidOperationException("Bluetooth LE watcher was not created");
            startingWatcher.Start();
            var stopAfterStart = false;
            lock (Gate)
            {
                if (_enabled && ReferenceEquals(_watcher, startingWatcher)) _startError = null;
                else stopAfterStart = true;
            }
            if (stopAfterStart) startingWatcher.Stop();
        }
        catch (Exception ex)
        {
            lock (Gate)
                if (_enabled) _startError = $"Windows could not start Bluetooth LE scanning (0x{ex.HResult:X8})";
        }
        finally
        {
            Interlocked.Exchange(ref _startPending, 0);
        }
    }

    public static string? Signature
    {
        get
        {
            var reading = GetFreshReading();
            return reading?.Signature;
        }
    }

    public static IReadOnlyList<DeviceObservation> Apply(IReadOnlyList<DeviceObservation> observations)
    {
        var result = observations.ToArray();
        var candidates = result.Select((device, index) => (device, index))
            .Where(item => item.device.Kind == DeviceKind.Headphones
                && item.device.Name.Contains("AirPods", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (candidates.Length != 1) return result;

        var reading = GetFreshReading();
        var current = candidates[0].device;
        if (reading?.SummaryPercent is not int percent)
        {
            if (!current.CurrentBattery.HasValue)
            {
                var error = GetStartError();
                result[candidates[0].index] = current with
                {
                    Message = error ?? (current.Connection == LinkState.Connected
                        ? "Windows reports AirPods connected, but no battery is available. Waiting for an AirPods battery broadcast; open the case nearby or take the earbuds out."
                        : "AirPods are not connected to this PC, and no nearby battery broadcast has been detected. Open the case near the PC or take the earbuds out."),
                    Provider = error is null ? current.Provider : "Apple BLE scanner unavailable"
                };
            }
            return result;
        }

        var components = new BatteryComponents(reading.Left, reading.Right, reading.Case,
            reading.LeftCharging, reading.RightCharging, reading.CaseCharging, reading.SingleBattery, reading.SeenUtc);
        var exactWindowsValue = current.CurrentBattery.HasValue;
        result[candidates[0].index] = current with
        {
            CurrentBattery = exactWindowsValue ? current.CurrentBattery : percent,
            IsCharging = exactWindowsValue ? current.IsCharging : reading.SummaryCharging,
            Message = exactWindowsValue
                ? "Windows provides an overall battery value; separate AirPods batteries come from an approximate BLE broadcast. The strongest recent nearby signal is used."
                : current.Connection == LinkState.Connected
                    ? "AirPods battery broadcast; approximate 10% steps. BLE addresses rotate, so the strongest recent nearby signal is used."
                    : "AirPods battery broadcast received nearby; Windows does not report this device as connected to this PC. The strongest recent nearby signal is used.",
            Provider = exactWindowsValue ? "Windows Bluetooth + Apple BLE battery advertisement" : "Apple BLE battery advertisement",
            CurrentBatteryIsEstimated = exactWindowsValue ? current.CurrentBatteryIsEstimated : true,
            BatteryComponents = components
        };
        return result;
    }

    private static AirPodsAdvertisementReading? GetFreshReading()
    {
        lock (Gate)
            return _latest is { } latest && DateTimeOffset.UtcNow - latest.SeenUtc <= TimeSpan.FromSeconds(60)
                ? latest : null;
    }

    private static string? GetStartError()
    {
        lock (Gate) return _startError;
    }

    private static void OnReceived(BluetoothLEAdvertisementWatcher sender,
        BluetoothLEAdvertisementReceivedEventArgs args)
    {
        try
        {
            foreach (var manufacturerData in args.Advertisement.GetManufacturerDataByCompanyId(AirPodsAdvertisementParser.AppleCompanyId))
            {
                var payload = new byte[checked((int)manufacturerData.Data.Length)];
                DataReader.FromBuffer(manufacturerData.Data).ReadBytes(payload);
                var now = DateTimeOffset.UtcNow;
                var reading = AirPodsAdvertisementParser.Parse(payload, args.RawSignalStrengthInDBm, now);
                if (reading == null) continue;

                lock (Gate)
                {
                    var stale = _latest == null || now - _latest.SeenUtc > TimeSpan.FromSeconds(10);
                    if (stale || reading.Rssi >= _latest!.Rssi - 5) _latest = reading;
                }
            }
        }
        catch (Exception)
        {
            // Advertisement parsing runs on a Windows callback thread; malformed packets are ignored.
        }
    }

    public static void Stop()
    {
        BluetoothLEAdvertisementWatcher? watcher;
        lock (Gate)
        {
            _enabled = false;
            if (_watcher == null) return;
            watcher = _watcher;
            _watcher = null;
            _latest = null;
            _startError = null;
        }
        try { watcher.Received -= OnReceived; watcher.Stop(); }
        catch (Exception) { }
    }
}
