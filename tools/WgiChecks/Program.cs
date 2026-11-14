using System.Text.Json;
using BatteryDesk;
using BatteryDesk.Hardware;
using Windows.System.Power;

// Synthetic values and pure projection only: no WGI enumeration or native I/O.
var checks = new List<(string Name, Action Run)>();
void Check(string name, Action run) => checks.Add((name, run));
void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

const string legacyA = "xinput:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
const string legacyB = "xinput:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
var containerA = Guid.Parse("11111111-2222-4333-8444-555555555555");
var containerB = Guid.Parse("66666666-7777-4888-8999-AAAAAAAAAAAA");
HidEndpoint Endpoint(ushort product, string suffix) => new(@"\\?\hid#vid_045e&pid_" + product.ToString("x4") + "&ig_00#" + suffix,
    0x045e, product, 1, 5, 16, 0, 0, "Controller (Xbox One For Windows)", "");
var endpointA = Endpoint(0x0b12, "controller-a");
var endpointB = Endpoint(0x0b00, "controller-b");
WindowsGamingInputProvider.ControllerReading Reading(string id = "nri-a", ushort product = 0x0b12, int? remaining = 700,
    int? full = 1000, BatteryStatus? status = BatteryStatus.Discharging, bool failed = false) =>
    new(id, 0x045e, product, true, remaining, full, status, failed);
XInputProvider.ControllerIdentity Identity(HidEndpoint endpoint) => endpoint.ProductId == 0x0b12
    ? new(legacyA, true, containerA) : new(legacyB, true, containerB);
IReadOnlyList<DeviceObservation> Project(IReadOnlyList<WindowsGamingInputProvider.ControllerReading> readings,
    EndpointIdentityMap? identities = null, IReadOnlyList<HidEndpoint>? endpoints = null) =>
    WindowsGamingInputProvider.BuildObservations(readings, endpoints ?? [endpointA, endpointB], identities ?? new(), Identity);

Check("Two capacity reports yield independently labeled estimated percentages", () =>
{
    var rows = Project([Reading(), Reading("nri-b", 0x0b00, 1000)]);
    Require(rows.Count == 2 && rows.All(r => r.Connection == LinkState.Connected && r.CurrentBatteryIsEstimated),
        "Multiple devices were hidden or estimates were mislabeled");
    Require(rows.Single(r => r.Id == legacyA).CurrentBattery == 70 && rows.Single(r => r.Id == legacyB).CurrentBattery == 100,
        "Capacity ratios were swapped or incorrect");
    Require(rows.All(r => r.CurrentBatteryLevel is null && r.Transport == "Xbox Wireless Adapter"), "Unexpected fallback metadata");
});
Check("Zero and full capacity remain valid estimated readings", () =>
{
    foreach (var remaining in new[] { 0, 1000 })
        Require(WindowsGamingInputProvider.TryEstimatePercent(remaining, 1000, out var value) && value == remaining / 10,
            "Zero/full capacity rejected");
});
Check("Null, negative, zero full, and overfull capacity cannot fabricate a percentage", () =>
{
    foreach (var (remaining, full) in new (int?, int?)[] { (null, 1000), (700, null), (-1, 1000), (700, 0), (700, -1), (1001, 1000) })
        Require(!WindowsGamingInputProvider.TryEstimatePercent(remaining, full, out _), "Invalid capacity accepted");
});
Check("Rounding is stable and large capacities do not overflow", () =>
{
    Require(WindowsGamingInputProvider.TryEstimatePercent(1, 8, out var rounded) && rounded == 13, "Half percentage rounded unexpectedly");
    Require(WindowsGamingInputProvider.TryEstimatePercent(int.MaxValue, int.MaxValue, out var full) && full == 100,
        "Large capacity overflowed");
});
Check("One failed battery query does not hide either connected controller", () =>
{
    var rows = Project([Reading(failed: true), Reading("nri-b", 0x0b00, 1000)]);
    var failed = rows.Single(r => r.Id == legacyA);
    Require(failed.Connection == LinkState.Connected && failed.CurrentBattery is null && !failed.CurrentBatteryIsEstimated,
        "Failed query became disconnected, fresh, or numeric");
    Require(rows.Single(r => r.Id == legacyB).CurrentBattery == 100, "Other controller lost valid reading");
});
Check("Missing battery report does not invent zero or charging state", () =>
{
    var row = Project([Reading(remaining: null, full: null, status: null)]).Single();
    Require(row.Connection == LinkState.Connected && row.CurrentBattery is null && row.IsCharging is null && !row.CurrentBatteryIsEstimated,
        "Missing report fabricated metadata");
});
Check("NotPresent battery status rejects contradictory capacity data", () =>
    Require(Project([Reading(status: BatteryStatus.NotPresent)]).Single().CurrentBattery is null,
        "No-battery report became numeric"));
Check("Charging state comes only from the system report", () =>
{
    Require(Project([Reading(status: BatteryStatus.Charging)]).Single().IsCharging == true, "Charging report lost");
    Require(Project([Reading(status: null)]).Single().IsCharging is null, "Unknown charging state fabricated");
});
Check("Two same-model controllers retain separate WGI identities without slot guessing", () =>
{
    var calls = 0;
    var rows = WindowsGamingInputProvider.BuildObservations([Reading("same-model-a"), Reading("same-model-b", remaining: 200)],
        [endpointA, endpointA with { Path = endpointA.Path + "second" }], new(), e => { calls++; return Identity(e); });
    Require(rows.Count == 2 && rows.Select(r => r.Id).Distinct().Count() == 2 && rows.All(r => r.Id.StartsWith("wgi:")),
        "Same-model controllers were merged or assigned legacy identities by order");
    Require(calls == 0 && rows.Any(r => r.CurrentBattery == 70) && rows.Any(r => r.CurrentBattery == 20),
        "Ambiguous HID metadata was used or readings were lost");
});
Check("Controller enumeration ordering cannot change identity or battery ownership", () =>
{
    var identities = new EndpointIdentityMap();
    var a = Reading("same-a"); var b = Reading("same-b", remaining: 200);
    var first = Project([a, b], identities);
    var next = Project([b, a], identities);
    Require(first.All(r => next.Single(n => n.Id == r.Id).CurrentBattery == r.CurrentBattery), "Enumeration order transferred battery ownership");
});
Check("First unique-model mapping reuses the installed XInput cache ID", () =>
{
    var identities = new EndpointIdentityMap();
    var row = Project([Reading()], identities).Single();
    Require(row.Id == legacyA, "Installed cache ID was replaced");
    Require(identities.Assignments.Values.Count(id => id == legacyA) == 2, "NRI and container aliases were not both saved");
});
Check("NUL suffixes do not split WGI identity", () =>
{
    var identities = new EndpointIdentityMap();
    Require(identities.ResolveWgiController("nri-a\0\0", legacyA, containerA)
        == identities.ResolveWgiController("nri-a"), "Trailing NUL created a second device");
});
Check("New connection identity resolves through unchanged physical ContainerId", () =>
{
    var identities = new EndpointIdentityMap();
    var initial = identities.ResolveWgiController("nri-before", legacyA, containerA);
    var reconnected = identities.ResolveWgiController("nri-after", "xinput:different-slot-parent", containerA);
    Require(initial == reconnected, "Receiver slot change lost physical device identity");
});
Check("Persisted identity aliases survive restart", () =>
{
    var original = new EndpointIdentityMap();
    original.ResolveWgiController("nri-before", legacyA, containerA);
    var saved = JsonSerializer.Deserialize<Dictionary<string, string>>(JsonSerializer.Serialize(original.Assignments));
    var restored = new EndpointIdentityMap(saved);
    Require(restored.ResolveWgiController("nri-before") == legacyA
        && restored.ResolveWgiController("nri-after", legacyB, containerA) == legacyA,
        "Restart forgot native or container identity aliases");
});
Check("A legacy ID already owned by another physical controller cannot be reused", () =>
{
    var identities = new EndpointIdentityMap();
    var a = identities.ResolveWgiController("nri-a", legacyA, containerA);
    var b = identities.ResolveWgiController("nri-b", legacyA, containerB);
    Require(a != b && b.StartsWith("wgi:"), "Conflicting legacy ID transferred another controller's cache");
});
Check("HID matching failure preserves the connected WGI controller and reading", () =>
{
    var rows = WindowsGamingInputProvider.BuildObservations([Reading()], [endpointA], new(),
        _ => throw new InvalidOperationException("Synthetic PnP metadata failure"));
    Require(rows.Single().CurrentBattery == 70 && rows.Single().Id.StartsWith("wgi:"), "PnP metadata failure discarded WGI data");
});
Check("Duplicate native IDs are only projected once", () =>
    Require(Project([Reading(), Reading("nri-a\0")]).Count == 1, "Duplicate native ID created duplicate observation"));
Check("Non-Microsoft raw controllers are left to their existing provider", () =>
    Require(Project([Reading() with { VendorId = 0x054c }]).Count == 0, "Provider claimed an unsupported vendor"));
Check("Empty native identity cannot silently merge controllers", () =>
{
    Require(Project([Reading("\0\0")]).Count == 0, "Blank native ID became a device");
    var rejected = false;
    try { new EndpointIdentityMap().ResolveWgiController("\0"); } catch (ArgumentException) { rejected = true; }
    Require(rejected, "Identity map accepted blank native ID");
});

var failed = 0;
foreach (var (name, run) in checks)
{
    try { run(); Console.WriteLine($"PASS {name}"); }
    catch (Exception exception)
    {
        failed++;
        Console.Error.WriteLine($"FAIL {name}: {exception.Message}");
    }
}
Console.WriteLine($"WGI checks: {checks.Count - failed}/{checks.Count} passed; {failed} failed. No hardware I/O performed.");
return failed == 0 ? 0 : 1;
