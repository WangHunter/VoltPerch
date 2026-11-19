using BatteryDesk;
using BatteryDesk.Hardware;

// Only synthetic observations and pure identity mapping. Never call Read,
// GetConnectionSignature, HID enumeration, or any native hardware function.
var checks = new List<(string Name, Action Run)>();
void Check(string name, Action run) => checks.Add((name, run));
void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

const string path = @"\\?\hid#vid_045e&pid_0b12&ig_00#7&1234567&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";
const string parent = @"USB\VID_045E&PID_0B12&IG_00\00&00&0000123456789ABC";
const string receiver = @"USB\VID_045E&PID_02E6\000000000";
var pad = new HidEndpoint(path, 0x045e, 0x0b12, 1, 5, 16, 0, 0, "Controller (Xbox One For Windows)", "");
XInputProvider.ControllerIdentity Identity(HidEndpoint endpoint) => XInputProvider.CreateIdentity(endpoint, parent, receiver);
XInputProvider.SlotReading[] Slots(uint active = 0, byte type = 2, byte level = 2, uint batteryResult = 0) =>
    Enumerable.Range(0, 4).Select(i => new XInputProvider.SlotReading((uint)i, i == active ? 0u : 1167u,
        i == active ? batteryResult : 1167u, type, level)).ToArray();
DeviceObservation Read(XInputProvider.SlotReading[] slots) => XInputProvider.BuildObservations([pad], slots, Identity).Single();

Check("Alkaline medium response remains discrete and never fabricates percent", () =>
{
    var observation = Read(Slots());
    Require(observation.Connection == LinkState.Connected && observation.CurrentBatteryLevel == BatteryLevel.Medium,
        "Valid response did not become a connected medium reading");
    Require(observation.CurrentBattery is null && observation.IsCharging is null, "Percentage or charging was invented");
    Require(observation.Transport == "Xbox Wireless Adapter", "Verified receiver ancestry lost");
});
Check("All four valid levels are accepted for both known battery types", () =>
{
    foreach (byte type in new byte[] { 2, 3 })
    foreach (byte level in new byte[] { 0, 1, 2, 3 })
        Require(Read(Slots(type: type, level: level)).CurrentBatteryLevel == (BatteryLevel)level, "Valid level rejected");
});
Check("Unknown, disconnected and wired battery types cannot become valid levels", () =>
{
    foreach (byte type in new byte[] { 0, 1, 4, 255 })
    {
        var observation = Read(Slots(type: type));
        Require(observation.CurrentBatteryLevel is null && observation.CurrentBattery is null,
            "Unsupported battery type became a reading");
    }
});
Check("Out-of-range level cannot become a battery reading", () =>
    Require(Read(Slots(level: 4)).CurrentBatteryLevel is null, "Invalid level accepted"));
Check("Battery query failure preserves independently confirmed connection", () =>
{
    var observation = Read(Slots(batteryResult: 1167));
    Require(observation.Connection == LinkState.Connected && observation.CurrentBatteryLevel is null,
        "Battery query failure changed connection or produced data");
});
Check("Four disconnected slots mark candidate disconnected without fresh battery", () =>
{
    var observation = Read(Slots(active: 99));
    Require(observation.Connection == LinkState.Disconnected && observation.CurrentBatteryLevel is null,
        "Receiver presence was mistaken for connection");
});
Check("Absent native interface remains unknown", () =>
    Require(Read([]).Connection == LinkState.Unknown, "Missing interface asserted disconnection"));
Check("Unexpected state error does not claim a disconnected device", () =>
{
    var slots = Slots(active: 99);
    slots[1] = slots[1] with { StateResult = 5 };
    Require(Read(slots).Connection == LinkState.Unknown, "State error treated as confirmed disconnection");
});
Check("Multiple active slots cannot assign a battery to the wrong physical controller", () =>
{
    var slots = Slots();
    slots[2] = slots[2] with { StateResult = 0, BatteryResult = 0, Level = 0 };
    var observation = Read(slots);
    Require(observation.Connection == LinkState.Unknown && observation.CurrentBatteryLevel is null,
        "Ambiguous slot assigned to controller");
});
Check("Multiple HID candidates remain ambiguous with only one active slot", () =>
{
    var second = pad with { Path = path.Replace("1234567", "1234568") };
    var rows = XInputProvider.BuildObservations([pad, second], Slots(), e => XInputProvider.CreateIdentity(e, null, receiver));
    Require(rows.Count == 2 && rows.All(r => r.Connection == LinkState.Unknown && r.CurrentBatteryLevel is null),
        "Ambiguous physical identity assigned a slot reading");
    Require(rows.Select(r => r.Id).Distinct().Count() == 2, "Different candidates shared identity");
});
Check("A slot belonging to a WGI controller cannot be assigned to a remaining receiver", () =>
{
    var receiverEndpoint = pad with { VendorId = 0x1234, Product = "Third-party controller" };
    var row = XInputProvider.BuildObservations([receiverEndpoint], Slots(), Identity, allowSlotMatch: false).Single();
    Require(row.Connection == LinkState.Unknown && row.CurrentBatteryLevel is null,
        "The remaining receiver received the globally active controller's battery level");
});
Check("No candidate never creates an invented physical controller", () =>
    Require(XInputProvider.BuildObservations([], Slots(), Identity).Count == 0, "Slot-only device invented"));
Check("Unrelated HID keyboards and non-XInput gamepads are excluded", () =>
{
    var keyboard = pad with { Usage = 6 };
    var nonXInput = pad with { Path = path.Replace("&ig_00", "") };
    Require(XInputProvider.BuildObservations([keyboard, nonXInput], Slots(), Identity).Count == 0,
        "Unrelated HID endpoint was bound to XInput");
});
Check("Duplicate endpoint enumeration does not cause false multi-device ambiguity", () =>
{
    var rows = XInputProvider.BuildObservations([pad, pad with { Path = path.ToUpperInvariant() }], Slots(), Identity);
    Require(rows.Count == 1 && rows[0].Connection == LinkState.Connected, "Duplicate path counted as separate controller");
});
Check("Multiple HID collections sharing a physical identity never duplicate a cache key", () =>
{
    var second = pad with { Path = path.Replace("1234567", "1234568") };
    var rows = XInputProvider.BuildObservations([pad, second], Slots(), Identity);
    Require(rows.Count == 1 && rows[0].Connection == LinkState.Unknown && rows[0].CurrentBatteryLevel is null,
        "Duplicate physical identity emitted duplicate rows or inferred an ambiguous slot");
});
Check("Physical parent identity survives slot reassignment and HID path changes", () =>
{
    var first = Read(Slots());
    var reassigned = Read(Slots(active: 3));
    var changedPath = XInputProvider.CreateIdentity(pad with { Path = path + "different-port" }, parent.ToLowerInvariant(), receiver);
    Require(first.Id == reassigned.Id && first.Id == changedPath.Id, "Slot or HID path replaced available parent identity");
});
Check("Different physical controller identities never collapse into their shared receiver", () =>
{
    var second = XInputProvider.CreateIdentity(pad, parent + "2", receiver);
    Require(Identity(pad).Id != second.Id, "Shared receiver merged controller IDs");
});
Check("Unavailable or receiver-only parent uses normalized full HID path", () =>
{
    var fallback = XInputProvider.CreateIdentity(pad, null, null);
    var upper = XInputProvider.CreateIdentity(pad with { Path = path.ToUpperInvariant() }, null, null);
    var sharedReceiver = XInputProvider.CreateIdentity(pad, receiver, null);
    Require(fallback.Id == upper.Id && fallback.Id == sharedReceiver.Id, "Fallback was unstable or used receiver identity");
    Require(fallback.Id != XInputProvider.CreateIdentity(pad with { Path = path + "different-port" }, null, null).Id,
        "Different fallback endpoints merged");
});
Check("Known receiver ancestry takes precedence over misleading wired battery flag", () =>
    Require(Read(Slots(type: 1)).Transport == "Xbox Wireless Adapter", "Confirmed receiver mislabeled USB wired"));
Check("Other controllers only use wired transport when battery API confirms it", () =>
{
    XInputProvider.ControllerIdentity Other(HidEndpoint e) => XInputProvider.CreateIdentity(e, parent, null);
    Require(XInputProvider.BuildObservations([pad], Slots(type: 1), Other).Single().Transport == "USB wired", "Wired status lost");
    Require(XInputProvider.BuildObservations([pad], Slots(), Other).Single().Transport == "Wireless (XInput)", "Wireless status guessed as receiver");
    Require(XInputProvider.BuildObservations([pad], Slots(type: 255), Other).Single().Transport == "XInput", "Unknown battery type invented a transport");
});
Check("HID interface path converts to the exact PnP instance ID", () =>
{
    Require(XInputProvider.InstanceIdFromPath(path) == @"hid\vid_045e&pid_0b12&ig_00\7&1234567&0&0000",
        "Instance path conversion incorrect");
    Require(XInputProvider.InstanceIdFromPath("invalid") is null, "Malformed interface path accepted");
});
Check("Native read structures have the Windows ABI sizes", () =>
{
    foreach (var (name, size) in new[] { ("NativeGamepad", 12), ("NativeState", 16), ("NativeBattery", 2) })
    {
        var type = typeof(XInputProvider).GetNestedType(name, System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Native structure missing");
        Require(System.Runtime.InteropServices.Marshal.SizeOf(type) == size, name + " has an incorrect layout");
    }
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
Console.WriteLine($"XInput checks: {checks.Count - failed}/{checks.Count} passed; {failed} failed. No hardware I/O performed.");
return failed == 0 ? 0 : 1;
