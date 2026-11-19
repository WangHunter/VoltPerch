namespace BatteryDesk.Hardware;

// Test doubles only: this project does not include native device code or call SaveSettings.
public sealed record HidEndpoint(string Path, ushort VendorId, ushort ProductId = 0, string Serial = "");
public static class HidNative
{
    public static Func<IReadOnlyList<HidEndpoint>> Enumeration = () => Array.Empty<HidEndpoint>();
    public static IReadOnlyList<HidEndpoint> Enumerate() => Enumeration();
}
public static class TargetHidProvider
{
    public static Func<Task<IReadOnlyList<DeviceObservation>>> Read = () => Task.FromResult<IReadOnlyList<DeviceObservation>>(Array.Empty<DeviceObservation>());
    public static Task<IReadOnlyList<DeviceObservation>> ReadAsync(IReadOnlyList<HidEndpoint> endpoints, EndpointIdentityMap? identities = null) => Read();
}
public static class BluetoothProvider
{
    public static Func<IReadOnlyList<DeviceObservation>> Query = () => Array.Empty<DeviceObservation>();
    public static IReadOnlyList<DeviceObservation> Read() => Query();
}
public static class AirPodsAdvertisementProvider
{
    public static string? Signature => null;
    public static void Start() { }
    public static void Stop() { }
    public static IReadOnlyList<DeviceObservation> Apply(IReadOnlyList<DeviceObservation> observations) => observations;
}
public static class XInputProvider
{
    public static Func<IReadOnlyList<HidEndpoint>, IReadOnlyList<DeviceObservation>> Query = _ => Array.Empty<DeviceObservation>();
    public static Func<string> ConnectionSignature = () => "0000";
    public static bool AllowSlotMatch { get; private set; }
    public static IReadOnlyList<DeviceObservation> Read(IReadOnlyList<HidEndpoint> endpoints, bool allowSlotMatch = true)
    {
        AllowSlotMatch = allowSlotMatch;
        return Query(endpoints);
    }
    public static string GetConnectionSignature() => ConnectionSignature();
}
public static class WindowsGamingInputProvider
{
    public static Func<IReadOnlyList<HidEndpoint>, EndpointIdentityMap, Task<IReadOnlyList<DeviceObservation>>> Query =
        (_, _) => Task.FromResult<IReadOnlyList<DeviceObservation>>(Array.Empty<DeviceObservation>());
    public static Func<string> ConnectionSignature = () => "";
    public static Task<IReadOnlyList<DeviceObservation>> ReadAsync(IReadOnlyList<HidEndpoint> endpoints, EndpointIdentityMap identities) => Query(endpoints, identities);
    public static string GetConnectionSignature() => ConnectionSignature();
}
