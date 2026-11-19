using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace BatteryDesk.Hardware;

public static class TargetHidProvider
{
    public static async Task<IReadOnlyList<DeviceObservation>> ReadAsync(IReadOnlyList<HidEndpoint> endpoints,
        EndpointIdentityMap? identities = null)
    {
        identities ??= new EndpointIdentityMap();
        var output = new List<DeviceObservation>();
        var keyboards = endpoints.Where(d => d.VendorId == 0x320f && d.ProductId == 0x5088
            && d.Product.Contains("V98pro", StringComparison.OrdinalIgnoreCase)
            && d.UsagePage == 0xff1c && d.Usage == 0x92).OrderBy(d => d.Path).ToArray();
        var keyboardReadings = new Dictionary<string, DeviceObservation>();
        foreach (var endpoint in keyboards)
        {
            var id = identities.Resolve(EndpointIdentityMap.VgnKeyboardId, endpoint);
            if (keyboardReadings.TryGetValue(id, out var reading) && reading.Connection == LinkState.Connected) continue;
            var name = id == EndpointIdentityMap.VgnKeyboardId ? "VGN V98pro V2" : "VGN V98pro V2 · " + id[^4..];
            try
            {
                // VGN/Weisheng read-only power query. Fixed request is required by this firmware.
                var raw = await HidNative.QueryAsync(endpoint, [4, 0, 0, 0x1a, 6, 0, 0, 0], timeoutMs: 900);
                if (TryParseVgn(raw, out var percent, out var charging))
                    keyboardReadings[id] = new(id, name, DeviceKind.Keyboard, "2.4 GHz receiver", LinkState.Connected, percent, charging,
                        "Keyboard responded to the battery query", "VGN HID · Verified on this keyboard");
                else
                    keyboardReadings[id] = new(id, name, DeviceKind.Keyboard, "2.4 GHz receiver", LinkState.Unknown, null, null,
                        raw.Length > 3 && raw[3] == 0xff ? "Receiver connected; keyboard is asleep or unresponsive. Press a key, then refresh" : "Receiver connected; battery response is invalid",
                        "VGN HID · Waiting for a valid device response");
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException)
            {
                keyboardReadings[id] = new(id, name, DeviceKind.Keyboard, "2.4 GHz receiver", LinkState.Unknown, null, null,
                    "Receiver connected; keyboard is asleep or unresponsive. Press a key, then refresh", "VGN HID · Battery query failed");
            }
        }
        output.AddRange(keyboardReadings.Values);
        var mice = endpoints.Where(d => d.VendorId == 0x1038 && d.ProductId is 0x172b or 0x1726
            && d.UsagePage == 0xffc0 && d.Usage == 1).OrderBy(d => d.ProductId == 0x172b ? 0 : 1).ThenBy(d => d.Path).ToArray();
        foreach (var endpoint in mice)
        {
            var wired = endpoint.ProductId == 0x172b;
            var id = identities.Resolve("rival-650", endpoint);
            var name = id == "rival-650" ? "SteelSeries Rival 650" : "SteelSeries Rival 650 · " + id[^4..];
            var transport = wired ? "USB wired" : "2.4 GHz receiver";
            try
            {
                var raw = await HidNative.QueryAsync(endpoint, [0, 0xaa, 1], timeoutMs: 900);
                if (TryParseRival(raw, out var percent, out var charging))
                    output.Add(new(id, name, DeviceKind.Mouse, transport, LinkState.Connected, percent, charging,
                        "Mouse responded to the battery query", "Rival 650 HID · Verified in wired mode"));
                else
                    output.Add(new(id, name, DeviceKind.Mouse, transport, wired ? LinkState.Connected : LinkState.Unknown,
                        null, null, "Battery reading unavailable; keeping the last valid reading", "Rival 650 HID"));
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException)
            {
                output.Add(new(id, name, DeviceKind.Mouse, transport, wired ? LinkState.Connected : LinkState.Unknown,
                    null, null, wired ? "USB connected; battery query failed. Try refreshing again" : "Receiver connected; device is asleep or unresponsive", "Rival 650 HID"));
            }
        }
        return output;
    }

    public static bool TryParseVgn(byte[] raw, out int percent, out bool charging)
    {
        percent = 0; charging = false;
        if (raw.Length < 10 || raw[0] != 4 || raw[1] != 0 || raw[2] != 0 || raw[3] != 0x1a
            || raw[4] != 6 || raw[8] > 100 || raw[9] > 2) return false;
        percent = raw[8]; charging = raw[9] != 0;
        return true;
    }
    public static bool TryParseRival(byte[] raw, out int percent, out bool charging)
    {
        percent = 0; charging = false;
        // Win32 ReadFile retains the leading zero report ID (hidapi strips it).
        if (raw.Length < 4 || raw[0] != 0 || raw[1] > 100) return false;
        percent = raw[1]; charging = raw[3] != 0;
        return true;
    }
}
