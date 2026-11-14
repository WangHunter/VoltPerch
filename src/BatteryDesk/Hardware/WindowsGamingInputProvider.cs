using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Gaming.Input;
using Windows.System.Power;

namespace BatteryDesk.Hardware;

public static class WindowsGamingInputProvider
{
    private static readonly SemaphoreSlim InitializationLock = new(1, 1);
    private static bool _initialized;

    public static async Task<IReadOnlyList<DeviceObservation>> ReadAsync(IReadOnlyList<HidEndpoint> endpoints,
        EndpointIdentityMap identities)
    {
        await EnsureInitializedAsync().ConfigureAwait(false);
        var readings = new List<ControllerReading>();
        foreach (var item in Controllers())
        {
            try
            {
                var battery = item.Controller.TryGetBatteryReport();
                readings.Add(new(item.Id, item.VendorId, item.ProductId, item.Wireless,
                    battery?.RemainingCapacityInMilliwattHours, battery?.FullChargeCapacityInMilliwattHours,
                    battery?.Status, false));
            }
            catch (Exception)
            {
                // Enumeration independently confirms connection. One failed battery
                // report must not suppress the other controller's valid reading.
                readings.Add(new(item.Id, item.VendorId, item.ProductId, item.Wireless, null, null, null, true));
            }
        }
        return BuildObservations(readings, endpoints, identities, XInputProvider.ResolveIdentity);
    }

    public static string GetConnectionSignature()
    {
        try { return string.Join("|", Controllers().Select(c => c.Id).OrderBy(id => id, StringComparer.Ordinal)); }
        catch (Exception) { return "wgi-unavailable"; }
    }

    private static async Task EnsureInitializedAsync()
    {
        await InitializationLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_initialized) return;
            _ = RawGameController.RawGameControllers;
            // WGI populates its first enumeration asynchronously. Subsequent scans
            // use the live collection immediately; the connection signature also
            // catches a controller arriving after this short initialization window.
            await Task.Delay(1000).ConfigureAwait(false);
            _initialized = true;
        }
        finally { InitializationLock.Release(); }
    }

    private sealed record ControllerEntry(RawGameController Controller, string Id, ushort VendorId, ushort ProductId, bool Wireless);

    private static IReadOnlyList<ControllerEntry> Controllers()
    {
        var result = new List<ControllerEntry>();
        foreach (var controller in RawGameController.RawGameControllers.ToArray())
        {
            try
            {
                if (controller.HardwareVendorId != 0x045e || Gamepad.FromGameController(controller) is null) continue;
                var id = controller.NonRoamableId.TrimEnd('\0');
                if (string.IsNullOrWhiteSpace(id)) continue;
                result.Add(new(controller, id, controller.HardwareVendorId, controller.HardwareProductId, controller.IsWireless));
            }
            catch (Exception) { /* A device can disappear during enumeration. */ }
        }
        return result.DistinctBy(c => c.Id, StringComparer.Ordinal).ToArray();
    }

    internal sealed record ControllerReading(string NonRoamableId, ushort VendorId, ushort ProductId,
        bool Wireless, int? RemainingCapacity, int? FullCapacity, BatteryStatus? Status, bool QueryFailed);

    internal static IReadOnlyList<DeviceObservation> BuildObservations(IReadOnlyList<ControllerReading> readings,
        IReadOnlyList<HidEndpoint> endpoints, EndpointIdentityMap identities,
        Func<HidEndpoint, XInputProvider.ControllerIdentity> resolveIdentity)
    {
        var controllers = readings.Where(r => r.VendorId == 0x045e && !string.IsNullOrWhiteSpace(r.NonRoamableId.TrimEnd('\0')))
            .DistinctBy(r => r.NonRoamableId.TrimEnd('\0'), StringComparer.Ordinal).ToArray();
        var candidates = endpoints.Where(e => e.UsagePage == 1 && e.Usage is 4 or 5
                && e.Path.Contains("&ig_", StringComparison.OrdinalIgnoreCase))
            .DistinctBy(e => e.Path, StringComparer.OrdinalIgnoreCase).ToArray();
        var output = new List<DeviceObservation>();
        foreach (var reading in controllers)
        {
            XInputProvider.ControllerIdentity? matched = null;
            var sameModel = candidates.Where(e => e.VendorId == reading.VendorId && e.ProductId == reading.ProductId).ToArray();
            if (sameModel.Length == 1 && controllers.Count(r => r.VendorId == reading.VendorId && r.ProductId == reading.ProductId) == 1)
            {
                try { matched = resolveIdentity(sameModel[0]); }
                catch (Exception) { /* Keep the WGI identity if PnP metadata is unavailable. */ }
            }
            var id = identities.ResolveWgiController(reading.NonRoamableId, matched?.Id, matched?.ContainerId);
            var transport = matched?.XboxReceiver == true ? "Xbox Wireless Adapter"
                : reading.Wireless ? "Wireless (Windows Gaming Input)" : "Windows Gaming Input";
            var percent = !reading.QueryFailed && reading.Status != BatteryStatus.NotPresent
                && TryEstimatePercent(reading.RemainingCapacity, reading.FullCapacity, out var estimate) ? estimate : (int?)null;
            var charging = percent.HasValue && reading.Status is not null
                ? reading.Status == BatteryStatus.Charging : (bool?)null;
            var message = percent.HasValue
                ? "Estimated from system-reported remaining and full charge capacity; not an exact measured percentage"
                : reading.QueryFailed ? "Controller connected; the system battery report could not be read. Keeping the last valid reading"
                : reading.Status == BatteryStatus.NotPresent ? "Controller connected; the system reports no battery and provides no reading"
                : "Controller connected; the system provides no usable capacity data. Keeping the last valid reading";
            output.Add(new(id, "Xbox Wireless Controller · " + id[^6..], DeviceKind.Controller, transport, LinkState.Connected,
                percent, charging, message, "Windows Gaming Input · System estimate", CurrentBatteryIsEstimated: percent.HasValue));
        }
        return output;
    }

    internal static bool TryEstimatePercent(int? remaining, int? full, out int percent)
    {
        percent = 0;
        if (remaining is not >= 0 || full is not > 0 || remaining > full) return false;
        percent = (int)Math.Round(100d * remaining.Value / full.Value, MidpointRounding.AwayFromZero);
        return true;
    }
}
