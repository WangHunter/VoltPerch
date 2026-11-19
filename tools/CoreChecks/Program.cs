using System.Text.Json;
using BatteryDesk;
using BatteryDesk.Hardware;

// Pure parsing and state-transition checks. Do not call Enumerate, QueryAsync, or ReadAsync.
var now = new DateTimeOffset(2026, 11, 14, 10, 0, 0, TimeSpan.Zero);
var settings = new AppSettings { LowBatteryThreshold = 20, EnableNotifications = true };
var checks = new List<(string Name, Action Run)>();

void Check(string name, Action run) => checks.Add((name, run));
void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

byte[] Vgn(int percent, int charge = 0)
{
    var bytes = new byte[64];
    new byte[] { 4, 0, 0, 0x1a, 6, 0, 0, 0, (byte)percent, (byte)charge }.CopyTo(bytes, 0);
    return bytes;
}

byte[] AirPodsPacket(int modelId = 0x0120, int firstLevel = 6, int secondLevel = 3,
    int caseLevel = 5, int chargingFlags = 7, bool flip = true)
{
    var bytes = new byte[27];
    bytes[0] = 0x07;
    bytes[1] = 25;
    bytes[3] = (byte)(modelId >> 8);
    bytes[4] = (byte)modelId;
    bytes[5] = flip ? (byte)0 : (byte)0x20;
    bytes[6] = (byte)((firstLevel << 4) | secondLevel);
    bytes[7] = (byte)((chargingFlags << 4) | caseLevel);
    return bytes;
}

DeviceSnapshot Live(int percent = 79, string id = "keyboard", bool charging = false) =>
    new(id, id, DeviceKind.Keyboard, "2.4 GHz", LinkState.Connected, percent, charging,
        true, now.AddHours(-1), "Fresh response", "test");

DeviceObservation Observe(LinkState connection, int? percent = null, bool? charging = null) =>
    new("keyboard", "keyboard", DeviceKind.Keyboard, "2.4 GHz", connection, percent, charging,
        "Observation", "test");

DeviceSnapshot Merge(DeviceSnapshot? old, DeviceObservation observation) =>
    StatePolicy.Merge(old is null ? [] : [old], [observation], now).Single();

DeviceSnapshot Coarse(BatteryLevel level, string id = "keyboard") =>
    Live(id: id) with { Kind = DeviceKind.Controller, BatteryPercent = null, BatteryLevel = level };

DeviceObservation ObserveLevel(LinkState connection, BatteryLevel? level) =>
    Observe(connection) with { Kind = DeviceKind.Controller, CurrentBatteryLevel = level };

Check("VGN real 64-byte packet gives 79%, not charging", () =>
{
    Require(TargetHidProvider.TryParseVgn(Vgn(79), out var level, out var charging), "Real response rejected");
    Require(level == 79 && !charging, "Real response decoded incorrectly");
});
Check("VGN FF error cannot become a valid 0% reading", () =>
{
    var error = Vgn(0);
    error[3] = 0xff;
    var valid = TargetHidProvider.TryParseVgn(error, out var level, out _);
    Require(!valid, "FF response accepted");
    var state = Merge(null, Observe(LinkState.Unknown, valid ? level : null));
    Require(state.BatteryPercent is null && !state.IsLive, "Failed parse became a displayed percentage");
});
Check("VGN 0% is a valid live reading", () =>
{
    Require(TargetHidProvider.TryParseVgn(Vgn(0), out var level, out _), "Zero rejected");
    var state = Merge(Live(), Observe(LinkState.Connected, level, false));
    Require(state.IsLive && state.BatteryPercent == 0, "Zero lost during state merge");
});
Check("VGN accepts 100% and supported charging states", () =>
{
    foreach (var charge in new[] { 1, 2 })
        Require(TargetHidProvider.TryParseVgn(Vgn(100, charge), out var level, out var charging)
            && level == 100 && charging, $"Charge state {charge} decoded incorrectly");
});
Check("VGN rejects out-of-range percentage", () =>
    Require(!TargetHidProvider.TryParseVgn(Vgn(101), out _, out _), "101% accepted"));
Check("VGN rejects unrecognized charge state", () =>
    Require(!TargetHidProvider.TryParseVgn(Vgn(79, 3), out _, out _), "Unknown charge accepted"));
Check("VGN rejects truncated reports", () =>
{
    foreach (var length in new[] { 0, 1, 8, 9 })
        Require(!TargetHidProvider.TryParseVgn(Vgn(79)[..length], out _, out _), $"Length {length} accepted");
});
Check("VGN rejects unrelated report ID, command, sequence, or length header", () =>
{
    foreach (var (offset, value) in new[] { (0, 3), (1, 1), (2, 1), (3, 0x1b), (4, 0) })
    {
        var wrong = Vgn(79);
        wrong[offset] = (byte)value;
        Require(!TargetHidProvider.TryParseVgn(wrong, out _, out _), $"Invalid header at offset {offset} accepted");
    }
});
Check("Rival leading zero report ID gives 92%, charging", () =>
{
    var raw = new byte[64];
    new byte[] { 0, 92, 0, 1 }.CopyTo(raw, 0);
    Require(TargetHidProvider.TryParseRival(raw, out var level, out var charging)
        && level == 92 && charging, "Win32 leading zero was misinterpreted");
});
Check("Rival valid 0% is distinct from parse failure", () =>
    Require(TargetHidProvider.TryParseRival([0, 0, 0, 0], out var level, out var charging)
        && level == 0 && !charging, "Valid zero rejected"));
Check("Rival rejects out-of-range percentage", () =>
    Require(!TargetHidProvider.TryParseRival([0, 101, 0, 0], out _, out _), "101% accepted"));
Check("Rival rejects truncated or missing report-ID frames", () =>
{
    foreach (var raw in new byte[][] { [], [0], [0, 92, 0], [92, 0, 1, 0] })
        Require(!TargetHidProvider.TryParseRival(raw, out _, out _), "Malformed Rival response accepted");
});

Check("Explicit disconnect retains battery and original read time", () =>
{
    var old = Live(79, charging: true);
    var state = Merge(old, Observe(LinkState.Disconnected));
    Require(state.Connection == LinkState.Disconnected && state.BatteryPercent == 79 && !state.IsLive,
        "Disconnected cached state incorrect");
    Require(state.IsCharging is null && state.LastSeenUtc == old.LastSeenUtc, "Disconnect fabricated live metadata");
});
Check("Device absent from enumeration retains a stale snapshot", () =>
{
    var old = Live();
    var state = StatePolicy.Merge([old], [], now).Single();
    Require(state.Connection == LinkState.Disconnected && !state.IsLive && state.BatteryPercent == 79
        && state.LastSeenUtc == old.LastSeenUtc, "Missing device cache lost or marked live");
});
Check("Read failure while connected retains stale battery", () =>
{
    var old = Live(79, charging: true);
    var state = Merge(old, Observe(LinkState.Connected));
    Require(state.Connection == LinkState.Connected && state.BatteryPercent == 79 && !state.IsLive
        && state.IsCharging is null && state.LastSeenUtc == old.LastSeenUtc, "Failed read refreshed stale data");
});
Check("Receiver-only unknown state does not assert device connection", () =>
{
    var state = Merge(Live(), Observe(LinkState.Unknown));
    Require(state.Connection == LinkState.Unknown && state.BatteryPercent == 79 && !state.IsLive,
        "Receiver presence promoted to connection or lost cache");
});
Check("Reconnect replaces cached reading and restores live state", () =>
{
    var offline = Merge(Live(), Observe(LinkState.Disconnected));
    var state = Merge(offline, Observe(LinkState.Connected, 72, true));
    Require(state.IsLive && state.Connection == LinkState.Connected && state.BatteryPercent == 72
        && state.IsCharging == true && state.LastSeenUtc == now, "Reconnect did not refresh snapshot");
});
Check("Never-read unknown device has no percentage or last read time", () =>
{
    var state = Merge(null, Observe(LinkState.Unknown));
    Require(state.BatteryPercent is null && state.LastSeenUtc is null && !state.IsLive, "Unknown became zero or fresh");
});
Check("Invalid observations cannot replace last good battery", () =>
{
    foreach (var value in new[] { -1, 101 })
    {
        var state = Merge(Live(), Observe(LinkState.Connected, value, true));
        Require(state.BatteryPercent == 79 && !state.IsLive && state.IsCharging is null, $"Invalid {value}% replaced cache");
    }
});
Check("Unknown or disconnected observations cannot claim fresh numeric data", () =>
{
    foreach (var connection in new[] { LinkState.Unknown, LinkState.Disconnected })
    {
        var state = Merge(null, Observe(connection, 50, true));
        Require(state.BatteryPercent is null && !state.IsLive && state.IsCharging is null, "Unconfirmed reading became live");
    }
});

Check("Each valid controller battery level is live without inventing a percentage", () =>
{
    foreach (var level in Enum.GetValues<BatteryLevel>())
    {
        var state = Merge(null, ObserveLevel(LinkState.Connected, level));
        Require(state.IsLive && state.BatteryLevel == level && state.BatteryPercent is null
            && state.LastSeenUtc == now, $"Level {level} lost or converted to a percentage");
    }
});
Check("Controller disconnect or failed read retains its last level and read time", () =>
{
    var old = Coarse(BatteryLevel.Low) with { IsCharging = true };
    foreach (var connection in new[] { LinkState.Disconnected, LinkState.Unknown, LinkState.Connected })
    {
        var state = Merge(old, ObserveLevel(connection, null));
        Require(state.Connection == connection && state.BatteryLevel == BatteryLevel.Low
            && state.BatteryPercent is null && !state.IsLive && state.IsCharging is null
            && state.LastSeenUtc == old.LastSeenUtc, $"State {connection} refreshed or lost the cached level");
    }
});
Check("Controller absent from enumeration keeps its last coarse reading", () =>
{
    var old = Coarse(BatteryLevel.Medium);
    var state = StatePolicy.Merge([old], [], now).Single();
    Require(state.Connection == LinkState.Disconnected && !state.IsLive && state.BatteryLevel == BatteryLevel.Medium
        && state.BatteryPercent is null && state.LastSeenUtc == old.LastSeenUtc, "Absent controller lost its last level");
});
Check("Fresh percentage replaces and clears an older coarse reading", () =>
{
    var state = Merge(Coarse(BatteryLevel.Low), Observe(LinkState.Connected, 47));
    Require(state.IsLive && state.BatteryPercent == 47 && state.BatteryLevel is null
        && state.LastSeenUtc == now, "Fresh percentage left a conflicting coarse level");
});
Check("Fresh coarse reading replaces and clears an older percentage", () =>
{
    var state = Merge(Live(79), ObserveLevel(LinkState.Connected, BatteryLevel.Medium));
    Require(state.IsLive && state.BatteryLevel == BatteryLevel.Medium && state.BatteryPercent is null
        && state.LastSeenUtc == now, "Fresh level retained an obsolete percentage");
});
Check("A valid percentage wins when an observation supplies both representations", () =>
{
    var observation = Observe(LinkState.Connected, 65) with { CurrentBatteryLevel = BatteryLevel.Low };
    var state = Merge(Coarse(BatteryLevel.Empty), observation);
    Require(state.IsLive && state.BatteryPercent == 65 && state.BatteryLevel is null,
        "Conflicting representations were kept or coarse data displaced a valid percentage");
});
Check("Invalid coarse enum values cannot refresh either cached representation", () =>
{
    foreach (var invalid in new[] { (BatteryLevel)(-1), (BatteryLevel)4, (BatteryLevel)255 })
    foreach (var old in new[] { Coarse(BatteryLevel.Full), Live(79) })
    {
        var state = Merge(old, ObserveLevel(LinkState.Connected, invalid));
        Require(!state.IsLive && state.BatteryLevel == old.BatteryLevel && state.BatteryPercent == old.BatteryPercent
            && state.LastSeenUtc == old.LastSeenUtc, $"Invalid enum {(int)invalid} changed cached state");
    }
});
Check("A valid coarse reading can replace an invalid reported percentage", () =>
{
    var observation = ObserveLevel(LinkState.Connected, BatteryLevel.Low) with { CurrentBattery = 101 };
    var state = Merge(Live(), observation);
    Require(state.IsLive && state.BatteryLevel == BatteryLevel.Low && state.BatteryPercent is null,
        "Invalid percentage blocked a valid coarse reading or survived alongside it");
});
Check("Unconfirmed coarse observations never claim a fresh reading", () =>
{
    foreach (var connection in new[] { LinkState.Disconnected, LinkState.Unknown })
    {
        var state = Merge(null, ObserveLevel(connection, BatteryLevel.Full));
        Require(!state.IsLive && state.BatteryLevel is null && state.BatteryPercent is null
            && state.LastSeenUtc is null, "Unconfirmed controller level became live");
    }
});
Check("JSON restart preserves coarse readings without converting them to percentages", () =>
{
    var old = Coarse(BatteryLevel.Empty);
    var loaded = JsonSerializer.Deserialize<DeviceSnapshot[]>(JsonSerializer.Serialize(new[] { old }))!;
    var state = StatePolicy.Merge(loaded, [], now.AddDays(1)).Single();
    Require(state.BatteryLevel == BatteryLevel.Empty && state.BatteryPercent is null && !state.IsLive
        && state.LastSeenUtc == old.LastSeenUtc, "Restart converted or refreshed a cached empty level");
});
Check("Legacy percentage snapshots remain readable without the new optional level field", () =>
{
    var json = JsonSerializer.Serialize(Live()).Replace(",\"BatteryLevel\":null", "");
    var loaded = JsonSerializer.Deserialize<DeviceSnapshot>(json)!;
    Require(loaded.BatteryPercent == 79 && loaded.BatteryLevel is null, "Legacy snapshot schema stopped loading");
});

Check("Fresh estimated percentage replaces coarse data and preserves its provenance", () =>
{
    var state = Merge(Coarse(BatteryLevel.Medium), Observe(LinkState.Connected, 70)
        with { CurrentBatteryIsEstimated = true });
    Require(state.IsLive && state.BatteryPercent == 70 && state.BatteryLevel is null
        && state.BatteryIsEstimated && state.LastSeenUtc == now, "Estimated percentage lost its provenance or freshness");
});
Check("Estimated cache survives disconnect, unknown connection, and failed reads", () =>
{
    var old = Live(70) with { BatteryIsEstimated = true };
    foreach (var connection in new[] { LinkState.Connected, LinkState.Unknown, LinkState.Disconnected })
    {
        var state = Merge(old, Observe(connection));
        Require(!state.IsLive && state.BatteryPercent == 70 && state.BatteryIsEstimated
            && state.LastSeenUtc == old.LastSeenUtc, $"Estimated cache changed after {connection}");
    }
});
Check("Fresh precise percentage or coarse data clears an older estimate flag", () =>
{
    var old = Live(70) with { BatteryIsEstimated = true };
    var precise = Merge(old, Observe(LinkState.Connected, 63));
    Require(precise.IsLive && precise.BatteryPercent == 63 && !precise.BatteryIsEstimated,
        "Fresh precise reading retained estimate flag");
    var coarse = Merge(old, ObserveLevel(LinkState.Connected, BatteryLevel.Low)
        with { CurrentBatteryIsEstimated = true });
    Require(coarse.IsLive && coarse.BatteryPercent is null && coarse.BatteryLevel == BatteryLevel.Low
        && !coarse.BatteryIsEstimated, "Coarse-only data retained a percentage estimate flag");
});
Check("Invalid estimated observations cannot relabel a valid precise cache", () =>
{
    foreach (int? percent in new int?[] { null, -1, 101 })
    {
        var state = Merge(Live(), Observe(LinkState.Connected, percent) with { CurrentBatteryIsEstimated = true });
        Require(!state.IsLive && state.BatteryPercent == 79 && !state.BatteryIsEstimated,
            "Invalid estimate changed precise cached provenance");
    }
});
Check("Restart retains an absent estimated device with original timestamp and provenance", () =>
{
    var old = Live(70) with { BatteryIsEstimated = true };
    var loaded = JsonSerializer.Deserialize<DeviceSnapshot[]>(JsonSerializer.Serialize(new[] { old }))!;
    var state = StatePolicy.Merge(loaded, [], now.AddDays(1)).Single();
    Require(state.Connection == LinkState.Disconnected && !state.IsLive && state.BatteryPercent == 70
        && state.BatteryIsEstimated && state.LastSeenUtc == old.LastSeenUtc, "Restart lost cached estimate provenance");
});
Check("Old snapshot and observation JSON default to a non-estimated reading", () =>
{
    var snapshotJson = JsonSerializer.Serialize(Live()).Replace(",\"BatteryIsEstimated\":false", "");
    var observationJson = JsonSerializer.Serialize(Observe(LinkState.Connected, 79))
        .Replace(",\"CurrentBatteryIsEstimated\":false", "");
    Require(!JsonSerializer.Deserialize<DeviceSnapshot>(snapshotJson)!.BatteryIsEstimated
        && !JsonSerializer.Deserialize<DeviceObservation>(observationJson)!.CurrentBatteryIsEstimated,
        "Legacy JSON acquired an estimated reading flag");
});
Check("Device list sorts connection before device kind and name", () =>
{
    var observations = new List<DeviceObservation>();
    foreach (var connection in new[] { LinkState.Disconnected, LinkState.Unknown, LinkState.Connected })
    foreach (var kind in new[] { DeviceKind.Other, DeviceKind.Headphones, DeviceKind.Controller, DeviceKind.Mouse, DeviceKind.Keyboard })
    foreach (var name in new[] { "B", "A" })
        observations.Add(Observe(connection) with { Id = $"{connection}-{kind}-{name}", Name = name, Kind = kind });
    var expected = from connection in new[] { LinkState.Connected, LinkState.Unknown, LinkState.Disconnected }
        from kind in new[] { DeviceKind.Keyboard, DeviceKind.Mouse, DeviceKind.Controller, DeviceKind.Headphones, DeviceKind.Other }
        from name in new[] { "A", "B" }
        select $"{connection}-{kind}-{name}";
    Require(StatePolicy.Merge([], observations, now).Select(d => d.Id).SequenceEqual(expected),
        "Connected devices, including unread controllers, were buried below unknown or offline devices");
});

Check("Automatic tray target excludes cached, unknown, and disconnected devices", () =>
{
    var stale = Live(1, "cached") with { IsLive = false };
    var unknown = Live(2, "unknown") with { Connection = LinkState.Unknown };
    var disconnected = Live(3, "disconnected") with { Connection = LinkState.Disconnected };
    var unread = Live(4, "unread") with { BatteryPercent = null };
    Require(StatePolicy.TrayTarget([stale, unknown, disconnected, unread, Live(50, "valid")], "")?.Id == "valid",
        "Automatic tray chose stale or unconfirmed percentage");
});
Check("Automatic tray prefers lowest non-charging live device", () =>
    Require(StatePolicy.TrayTarget([Live(5, "charging", true), Live(30, "high"), Live(20, "low")], "")?.Id == "low",
        "Automatic tray priority incorrect"));
Check("Automatic tray with only cached readings has no live target", () =>
    Require(StatePolicy.TrayTarget([Live() with { IsLive = false }], "") is null, "Cached-only tray target accepted"));
Check("Selected offline tray device remains selected with stale metadata", () =>
{
    var offline = Merge(Live(), Observe(LinkState.Disconnected));
    var target = StatePolicy.TrayTarget([offline, Live(20, "other")], "keyboard");
    Require(target?.Id == "keyboard" && !target.IsLive && target.Connection == LinkState.Disconnected
        && target.BatteryPercent == 79, "Fixed selection lost cached explanation or silently switched devices");
});
Check("Fixed selection preserves unknown or unavailable state for tray rendering", () =>
{
    foreach (var state in new[] { Merge(Live(), Observe(LinkState.Unknown)), Merge(null, Observe(LinkState.Unknown)) })
    {
        var target = StatePolicy.TrayTarget([state], "keyboard");
        Require(target?.Id == "keyboard" && !target.IsLive && target.Connection == LinkState.Unknown
            && target.BatteryPercent == state.BatteryPercent, "Selection fabricated live battery metadata");
    }
});
Check("Selected eligible tray device and missing selection behave predictably", () =>
{
    Require(StatePolicy.TrayTarget([Live(79), Live(20, "other")], "keyboard")?.Id == "keyboard", "Eligible selection ignored");
    Require(StatePolicy.TrayTarget([Live()], "missing") is null, "Missing selection silently substituted");
});
Check("Automatic tray selects a valid percentage before even an empty coarse reading", () =>
    Require(StatePolicy.TrayTarget([Coarse(BatteryLevel.Empty, "controller"), Live(88, "percent")], "")?.Id == "percent",
        "A coarse level was converted or compared against an exact percentage"));
Check("Without percentages, automatic tray picks the lowest valid live coarse level", () =>
{
    var cached = Coarse(BatteryLevel.Empty, "cached") with { IsLive = false };
    var offline = Coarse(BatteryLevel.Empty, "offline") with { Connection = LinkState.Disconnected };
    var unknown = Coarse(BatteryLevel.Empty, "unknown") with { Connection = LinkState.Unknown };
    var invalid = Coarse((BatteryLevel)(-1), "invalid");
    var target = StatePolicy.TrayTarget([Coarse(BatteryLevel.Full, "full"), cached, offline, unknown,
        Coarse(BatteryLevel.Low, "low"), Coarse(BatteryLevel.Medium, "medium"), invalid], "");
    Require(target?.Id == "low", "Tray fallback selected stale/invalid data or a higher coarse level");
});
Check("Invalid percentages do not block a valid coarse tray fallback", () =>
    Require(StatePolicy.TrayTarget([Live(-1, "invalid-percent"), Coarse(BatteryLevel.Low, "coarse")], "")?.Id == "coarse",
        "Invalid numeric data was selected ahead of valid coarse data"));
Check("Cached or invalid coarse readings alone do not create an automatic tray target", () =>
    Require(StatePolicy.TrayTarget([Coarse(BatteryLevel.Low) with { IsLive = false }, Coarse((BatteryLevel)4, "invalid")], "") is null,
        "Automatic tray showed an unavailable coarse reading as current"));
Check("A fixed offline coarse device retains its identity and cached reading", () =>
{
    var old = Coarse(BatteryLevel.Low, "controller") with { Connection = LinkState.Disconnected, IsLive = false };
    var selected = StatePolicy.TrayTarget([Live(79), old], "controller");
    Require(selected?.Id == "controller" && selected.BatteryLevel == BatteryLevel.Low
        && selected.BatteryPercent is null && !selected.IsLive, "Fixed selection replaced or promoted cached coarse data");
});
Check("Automatic tray prefers a precise percentage over any estimated percentage", () =>
{
    var estimate = Live(1, "estimate") with { BatteryIsEstimated = true };
    Require(StatePolicy.TrayTarget([estimate, Live(90, "precise", charging: true)], "")?.Id == "precise",
        "Estimated and precise percentages were compared as equally accurate");
});
Check("Estimated tray fallback keeps charging priority and precedes coarse levels", () =>
{
    var estimates = new[] { Live(5, "charging", true), Live(70, "high"), Live(40, "low") }
        .Select(d => d with { BatteryIsEstimated = true });
    Require(StatePolicy.TrayTarget([Coarse(BatteryLevel.Empty, "coarse"), .. estimates], "")?.Id == "low",
        "Estimated tray fallback lost charging priority or was displaced by coarse data");
});
Check("Cached estimates are excluded automatically but preserved as fixed tray targets", () =>
{
    var cached = Live(10, "estimate") with { BatteryIsEstimated = true, IsLive = false, Connection = LinkState.Disconnected };
    var devices = new[] { cached, Coarse(BatteryLevel.Medium, "coarse") };
    Require(StatePolicy.TrayTarget(devices, "")?.Id == "coarse", "Automatic tray used a stale estimate");
    var selected = StatePolicy.TrayTarget(devices, "estimate");
    Require(selected?.Id == "estimate" && selected.BatteryIsEstimated && selected.BatteryPercent == 10 && !selected.IsLive,
        "Fixed selection lost estimate metadata or changed target");
});

Check("First alert fires at threshold and records its time", () =>
{
    var memory = new AlertMemory();
    Require(memory.ShouldNotify(Live(20), settings, now) && memory.LastPercent == 20 && memory.LastTime == now,
        "Initial threshold crossing did not notify");
});
Check("Above-threshold battery does not alert", () =>
    Require(!new AlertMemory().ShouldNotify(Live(21), settings, now), "Above-threshold alert"));
Check("Five-point drop still waits for thirty-minute cooldown", () =>
{
    var memory = new AlertMemory();
    Require(memory.ShouldNotify(Live(20), settings, now), "Initial alert missing");
    Require(!memory.ShouldNotify(Live(15), settings, now.AddMinutes(30).AddTicks(-1)), "Alert repeated before cooldown");
    Require(memory.ShouldNotify(Live(15), settings, now.AddMinutes(30)), "Alert missing at exact cooldown boundary");
});
Check("Cooldown alone does not repeat alert without five-point drop", () =>
{
    var memory = new AlertMemory();
    memory.ShouldNotify(Live(20), settings, now);
    Require(!memory.ShouldNotify(Live(16), settings, now.AddHours(1)), "Four-point drop repeated alert");
    Require(!memory.ShouldNotify(Live(20), settings, now.AddHours(2)), "Unchanged percentage repeated alert");
    Require(memory.LastPercent == 20 && memory.LastTime == now, "Suppressed observations changed alert baseline");
});
Check("Repeated alerts use most recent notified baseline", () =>
{
    var memory = new AlertMemory();
    memory.ShouldNotify(Live(20), settings, now);
    Require(memory.ShouldNotify(Live(15), settings, now.AddMinutes(30)), "Second alert missing");
    Require(!memory.ShouldNotify(Live(11), settings, now.AddMinutes(60)), "Only four-point subsequent drop alerted");
    Require(memory.ShouldNotify(Live(10), settings, now.AddMinutes(60)), "Five-point subsequent drop missing");
});
Check("Charging battery stops low-battery alerts", () =>
{
    var memory = new AlertMemory();
    Require(!memory.ShouldNotify(Live(10, charging: true), settings, now), "Charging device alerted");
    Require(memory.LastPercent is null && memory.LastTime is null, "Charging consumed alert eligibility");
});
Check("Cached or unconfirmed low readings never trigger alerts", () =>
{
    foreach (var invalid in new[] { Live(5) with { IsLive = false }, Live(5) with { Connection = LinkState.Unknown },
        Live(5) with { Connection = LinkState.Disconnected }, Live(5) with { BatteryPercent = null } })
        Require(!new AlertMemory().ShouldNotify(invalid, settings, now), "Stale or unconfirmed device alerted");
});
Check("Disabled notifications do not consume first-alert eligibility", () =>
{
    var memory = new AlertMemory();
    Require(!memory.ShouldNotify(Live(10), new AppSettings { EnableNotifications = false }, now), "Disabled notification fired");
    Require(memory.ShouldNotify(Live(10), settings, now), "Enabling notifications lost first alert");
});
Check("Hysteresis requires threshold plus five before rearming", () =>
{
    var memory = new AlertMemory();
    memory.ShouldNotify(Live(20), settings, now);
    Require(!memory.ShouldNotify(Live(24), settings, now.AddMinutes(1)), "Recovery produced alert");
    Require(!memory.ShouldNotify(Live(20), settings, now.AddHours(1)), "Four-point recovery prematurely rearmed");
    Require(!memory.ShouldNotify(Live(25), settings, now.AddHours(2)), "Recovery threshold produced alert");
    Require(memory.LastPercent is null && memory.LastTime is null, "Five-point recovery failed to reset memory");
    Require(memory.ShouldNotify(Live(20), settings, now.AddHours(2).AddMinutes(1)), "Recovered device did not alert on new drop");
});
Check("Cached high reading cannot rearm alert hysteresis", () =>
{
    var memory = new AlertMemory();
    memory.ShouldNotify(Live(20), settings, now);
    memory.ShouldNotify(Live(80) with { IsLive = false }, settings, now.AddHours(1));
    Require(memory.LastPercent == 20 && memory.LastTime == now, "Cached battery reset alert memory");
});
Check("Coarse battery levels never trigger or rearm percentage alerts", () =>
{
    var memory = new AlertMemory { LastPercent = 20, LastTime = now };
    foreach (var level in Enum.GetValues<BatteryLevel>())
        Require(!memory.ShouldNotify(Coarse(level), settings, now.AddHours(1)), $"Coarse {level} triggered a percentage alert");
    Require(memory.LastPercent == 20 && memory.LastTime == now, "Coarse data changed a percentage notification baseline");
});
Check("Estimated low readings do not notify or consume initial precise alert eligibility", () =>
{
    var memory = new AlertMemory();
    Require(!memory.ShouldNotify(Live(0) with { BatteryIsEstimated = true }, settings, now)
        && memory.LastPercent is null && memory.LastTime is null, "Estimated low value consumed alert eligibility");
    Require(memory.ShouldNotify(Live(20), settings, now), "Subsequent precise reading did not alert");
});
Check("Estimated readings cannot rearm or change an existing precise alert baseline", () =>
{
    var memory = new AlertMemory { LastPercent = 20, LastTime = now };
    foreach (var value in new[] { 100, 70, 20, 10, 0 })
        Require(!memory.ShouldNotify(Live(value) with { BatteryIsEstimated = true }, settings, now.AddHours(1)),
            $"Estimated {value}% triggered an exact-threshold alert");
    Require(memory.LastPercent == 20 && memory.LastTime == now, "Estimate altered precise notification baseline");
});
Check("JSON restart preserves last successful battery and read timestamp", () =>
{
    var old = Live(79);
    var json = JsonSerializer.Serialize(new[] { old });
    var loaded = JsonSerializer.Deserialize<DeviceSnapshot[]>(json) ?? throw new InvalidOperationException("No restored snapshots");
    Require(loaded.Single().BatteryPercent == 79 && loaded.Single().LastSeenUtc == old.LastSeenUtc, "Restart lost persisted reading");
    var afterRestart = StatePolicy.Merge(loaded, [], now.AddDays(1)).Single();
    Require(afterRestart.BatteryPercent == 79 && afterRestart.LastSeenUtc == old.LastSeenUtc && !afterRestart.IsLive
        && afterRestart.Connection == LinkState.Disconnected, "Restart promoted cached snapshot to fresh");
});

HidEndpoint Endpoint(string path, string serial = "", ushort product = 0x5088) =>
    new(path, 0x320f, product, 0xff1c, 0x92, 64, 64, 0, "Test keyboard", serial);

Check("First endpoint in each family preserves its legacy cache ID", () =>
{
    var identities = new EndpointIdentityMap();
    Require(identities.Resolve("vgn-v98pro-v2", Endpoint("path-a")) == "vgn-v98pro-v2", "Keyboard legacy cache ID changed");
    Require(identities.Resolve("rival-650", Endpoint("mouse-a")) == "rival-650", "Mouse legacy cache ID changed");
});
Check("Same-model endpoint identities survive enumeration reordering", () =>
{
    var identities = new EndpointIdentityMap();
    var a = Endpoint("path-a"); var b = Endpoint("path-b");
    var aId = identities.Resolve("keyboard", a); var bId = identities.Resolve("keyboard", b);
    Require(aId != bId, "Two endpoints shared an identity");
    Require(identities.Resolve("keyboard", b) == bId && identities.Resolve("keyboard", a) == aId,
        "Enumeration order changed endpoint identity");
});
Check("Removing first same-model device cannot transfer its cached reading", () =>
{
    var identities = new EndpointIdentityMap();
    var aId = identities.Resolve("keyboard", Endpoint("path-a"));
    var b = Endpoint("path-b"); var bId = identities.Resolve("keyboard", b);
    var observations = new[] { new DeviceObservation(identities.Resolve("keyboard", b), "B", DeviceKind.Keyboard,
        "2.4 GHz", LinkState.Unknown, null, null, "Receiver only", "test") };
    var merged = StatePolicy.Merge([Live(79, aId), Live(12, bId)], observations, now);
    Require(merged.Single(d => d.Id == aId) is { BatteryPercent: 79, Connection: LinkState.Disconnected },
        "First device cache was overwritten");
    Require(merged.Single(d => d.Id == bId) is { BatteryPercent: 12, Connection: LinkState.Unknown },
        "Remaining device inherited another device's reading");
});
Check("Persisted identities survive restart with first device absent", () =>
{
    var identities = new EndpointIdentityMap();
    identities.Resolve("keyboard", Endpoint("path-a"));
    var bId = identities.Resolve("keyboard", Endpoint("path-b"));
    var saved = JsonSerializer.Deserialize<Dictionary<string, string>>(JsonSerializer.Serialize(identities.Assignments));
    var restored = new EndpointIdentityMap(saved);
    Require(restored.Resolve("keyboard", Endpoint("path-b")) == bId, "Restart reassigned remaining device");
    Require(restored.Resolve("keyboard", Endpoint("path-c")) != "keyboard", "Restart reused removed device's legacy ID");
});
Check("Rebuilding a missing VGN port assignment respects the saved merge immediately", () =>
{
    const string family = EndpointIdentityMap.VgnKeyboardId;
    var identities = new EndpointIdentityMap();
    identities.Resolve(family, Endpoint("current-port"));
    var oldId = identities.Resolve(family, Endpoint("old-port"));
    identities.MergeVgn(oldId, family);
    var recovered = new EndpointIdentityMap(identities.Assignments
        .Where(pair => pair.Key != family + "|path:OLD-PORT").ToDictionary());
    Require(recovered.Resolve(family, Endpoint("old-port")) == family
        && recovered.Resolve(family, Endpoint("old-port")) == family,
        "Rebuilding the port assignment returned an obsolete device ID");
});
Check("Usable serial preserves identity across USB path changes", () =>
{
    var identities = new EndpointIdentityMap();
    var id = identities.Resolve("keyboard", Endpoint("port-a", " SERIAL-123 "));
    Require(identities.Resolve("keyboard", Endpoint("port-b", "serial-123")) == id, "Serial identity depended on port or letter case");
});
Check("Serial identity does not arbitrarily merge different product transports", () =>
{
    var identities = new EndpointIdentityMap();
    var wired = identities.Resolve("mouse", Endpoint("wired", "SERIAL-123", 0x172b));
    var receiver = identities.Resolve("mouse", Endpoint("receiver", "SERIAL-123", 0x1726));
    Require(wired != receiver, "Different product transports were merged without physical identity evidence");
});
Check("Without usable serial, full normalized path defines endpoint identity", () =>
{
    var identities = new EndpointIdentityMap();
    var id = identities.Resolve("keyboard", Endpoint("PORT-A", "000000"));
    Require(identities.Resolve("keyboard", Endpoint("port-a", "000000")) == id, "Path letter case changed identity");
    Require(identities.Resolve("keyboard", Endpoint("port-b", "000000")) != id, "Different no-serial ports unexpectedly shared identity");
});

Check("AirPods advertisement decodes left, right, case and charging flags", () =>
{
    var reading = AirPodsAdvertisementParser.Parse(AirPodsPacket(), -45, now);
    if (reading is null) throw new InvalidOperationException("AirPods packet was rejected");
    Require(reading is { Left: 60, Right: 30, Case: 50, LeftCharging: true,
        RightCharging: true, CaseCharging: true, SingleBattery: false }, "AirPods components decoded incorrectly");
    Require(reading.SummaryPercent == 30 && reading.SummaryCharging == true,
        "AirPods summary did not use the lower earbud reading");
});
Check("AirPods side flip changes component mapping", () =>
{
    var reading = AirPodsAdvertisementParser.Parse(AirPodsPacket(chargingFlags: 1, flip: false), -45, now);
    Require(reading is { Left: 30, Right: 60, LeftCharging: true, RightCharging: false },
        "AirPods flip flag mapped sides or charging incorrectly");
});
Check("AirPods Max advertisement decodes its single battery", () =>
{
    var reading = AirPodsAdvertisementParser.Parse(AirPodsPacket(modelId: 0x0A20,
        firstLevel: 2, secondLevel: 4, caseLevel: 0, chargingFlags: 1), -45, now);
    Require(reading is { Left: 40, Right: null, Case: null, SingleBattery: true, LeftCharging: true }
        && reading.SummaryPercent == 40, "AirPods Max battery decoded incorrectly");
});
Check("AirPods advertisement rejects malformed packets and weak signals", () =>
{
    var packet = AirPodsPacket();
    Require(AirPodsAdvertisementParser.Parse(packet, -76, now) is null, "Weak advertisement accepted");
    Require(AirPodsAdvertisementParser.Parse(packet[..26], -45, now) is null, "Truncated advertisement accepted");
    var wrongType = (byte[])packet.Clone();
    wrongType[0] = 0x08;
    Require(AirPodsAdvertisementParser.Parse(wrongType, -45, now) is null, "Unrelated advertisement accepted");
    var wrongLength = (byte[])packet.Clone();
    wrongLength[1] = 24;
    Require(AirPodsAdvertisementParser.Parse(wrongLength, -45, now) is null, "Wrong packet length accepted");
});
Check("AirPods unknown level nibble is not converted to zero", () =>
{
    var reading = AirPodsAdvertisementParser.Parse(AirPodsPacket(firstLevel: 15, secondLevel: 4), -45, now);
    Require(reading is { Left: null, Right: 40, Case: 50 } && AirPodsAdvertisementParser.DecodeLevel(15) is null,
        "Unknown battery nibble became a percentage");
});
Check("Fresh AirPods nearby reading is retained without claiming a PC connection", () =>
{
    var components = new BatteryComponents(60, 60, 60, true, false, false, SeenUtc: now);
    var observation = new DeviceObservation("airpods", "AirPods", DeviceKind.Headphones, "Bluetooth",
        LinkState.Disconnected, 60, true, "Nearby broadcast", "Apple BLE battery advertisement",
        CurrentBatteryIsEstimated: true, BatteryComponents: components);
    var state = Merge(null, observation);
    Require(state.Connection == LinkState.Disconnected && !state.IsLive && state.BatteryPercent == 60
        && state.BatteryIsEstimated && state.LastSeenUtc == now && state.BatteryComponents == components,
        "Nearby AirPods battery was discarded or shown as connected/live");
});
Check("AirPods last reading and components survive later disconnect observations", () =>
{
    var components = new BatteryComponents(60, 60, 60, true, false, false, SeenUtc: now);
    var observation = new DeviceObservation("airpods", "AirPods", DeviceKind.Headphones, "Bluetooth",
        LinkState.Disconnected, 60, true, "Nearby broadcast", "Apple BLE battery advertisement",
        CurrentBatteryIsEstimated: true, BatteryComponents: components);
    var first = Merge(null, observation);
    var later = Merge(first, new DeviceObservation("airpods", "AirPods", DeviceKind.Headphones, "Bluetooth",
        LinkState.Disconnected, null, null, "No fresh broadcast", "Apple BLE battery advertisement"));
    var restored = JsonSerializer.Deserialize<DeviceSnapshot>(JsonSerializer.Serialize(later))!;
    Require(later.BatteryPercent == 60 && later.BatteryComponents == components && !later.IsLive
        && later.LastSeenUtc == now, "AirPods offline cache changed after a later scan");
    Require(restored.BatteryPercent == 60 && restored.BatteryComponents == components,
        "AirPods component cache did not survive JSON persistence");
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
Console.WriteLine($"Core checks: {checks.Count - failed}/{checks.Count} passed; {failed} failed. No hardware I/O performed.");
return failed == 0 ? 0 : 1;
