using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace BatteryDesk.Hardware;

/// <summary>
/// Keeps device/cache IDs independent of current enumeration order. Without a usable USB serial,
/// identity uses the full device path: moving to a USB port that changes this path creates a separate row.
/// Different VID/PID transports are deliberately not merged without evidence of physical identity.
/// </summary>
public sealed class EndpointIdentityMap
{
    private readonly Dictionary<string, string> _assignments;
    public IReadOnlyDictionary<string, string> Assignments => _assignments;

    public EndpointIdentityMap(IDictionary<string, string>? saved = null)
    {
        _assignments = saved == null ? new(StringComparer.Ordinal) : new(saved, StringComparer.Ordinal);
    }

    public string Resolve(string family, HidEndpoint endpoint)
    {
        var serial = endpoint.Serial.Trim().ToUpperInvariant();
        var usableSerial = serial.Length > 0 && serial is not ("UNKNOWN" or "NONE" or "N/A" or "DEFAULT")
            && serial.Any(c => char.IsLetterOrDigit(c) && c != '0');
        var identity = usableSerial
            ? $"serial:{endpoint.VendorId:X4}:{endpoint.ProductId:X4}:{serial}"
            : "path:" + endpoint.Path.Trim().ToUpperInvariant();
        var key = family + "|" + identity;
        if (_assignments.TryGetValue(key, out var existing)) return existing;

        // The first endpoint preserves the pre-identity-map cache ID. Its assignment is never
        // removed when absent, so another device cannot later inherit this legacy ID or reading.
        var id = _assignments.Keys.Any(k => k.StartsWith(family + "|", StringComparison.Ordinal))
            ? family + "-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..12]
            : family;
        _assignments.Add(key, id);
        return id;
    }

    public string ResolveWgiController(string nonRoamableId, string? legacyXInputId = null, Guid? containerId = null)
    {
        var nativeId = nonRoamableId.TrimEnd('\0');
        if (string.IsNullOrWhiteSpace(nativeId)) throw new ArgumentException("Controller has no persistent device identity", nameof(nonRoamableId));
        var key = "wgi|id:" + nativeId;
        var containerKey = containerId is Guid container && container != Guid.Empty
            ? "wgi|container:" + container.ToString("D") : null;
        if (_assignments.TryGetValue(key, out var existing))
        {
            if (containerKey is not null) _assignments.TryAdd(containerKey, existing);
            return existing;
        }

        string id;
        if (containerKey is not null && _assignments.TryGetValue(containerKey, out var aliased))
            id = aliased;
        else if (legacyXInputId?.StartsWith("xinput:", StringComparison.Ordinal) == true
            && !_assignments.Values.Contains(legacyXInputId, StringComparer.Ordinal))
            id = legacyXInputId;
        else
            id = "wgi:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(nativeId))).ToLowerInvariant();

        _assignments.Add(key, id);
        if (containerKey is not null) _assignments.TryAdd(containerKey, id);
        return id;
    }
}
