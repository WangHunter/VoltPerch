using System.Text;
using BatteryDesk.Hardware;

// Synthetic property data only. Never enumerate Bluetooth or query hardware.
var checks = new List<(string Name, Action Run)>();
void Check(string name, Action run) => checks.Add((name, run));
void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
byte[] Property(string value) => Encoding.Unicode.GetBytes(value + "\0");
const ulong jbl = 0x020000AABBCC;
const ulong second = 0x020000AABBCD;
const string hfpService = @"BTHENUM\{0000111E-0000-1000-8000-00805F9B34FB}_VID&00010ECB_PID&1F40\7&1234567&1&020000AABBCC_C00000000";
const string radioParent = @"BTH\MS_BTHBRB\6&7654321&0&1";
const string remote = @"BTHENUM\DEV_020000AABBCC\7&1234567&1&BLUETOOTHDEVICE_020000AABBCC";

Check("Sibling HFP address property identifies the remote device despite unrelated parent", () =>
{
    Require(BluetoothProvider.ParseAddress(hfpService) is null && BluetoothProvider.ParseAddress(radioParent) is null,
        "Service path or radio was guessed as a remote address");
    var address = BluetoothProvider.ParseAddressProperty(18, Property("020000AABBCC"), 26);
    Require(address == jbl, "Sibling HFP address property was rejected");
    var values = new Dictionary<ulong, HashSet<int>>();
    BluetoothProvider.AddBatteryValue(values, new HashSet<ulong> { jbl }, address,
        BluetoothProvider.ParseBattery(3, [50], 1));
    Require(values[jbl].SetEquals([50]), "Sibling HFP byte reading was lost");
});
Check("Byte battery boundaries retain zero and full", () =>
{
    foreach (byte value in new byte[] { 0, 50, 100 })
        Require(BluetoothProvider.ParseBattery(3, [value], 1) == value, "Valid byte reading rejected");
});
Check("Out-of-range byte battery is never clamped", () =>
{
    foreach (byte value in new byte[] { 101, 255 })
        Require(BluetoothProvider.ParseBattery(3, [value], 1) is null, "Invalid byte reading accepted");
});
Check("Supported Windows integer property types are still accepted", () =>
{
    Require(BluetoothProvider.ParseBattery(4, BitConverter.GetBytes((short)50), 2) == 50, "INT16 rejected");
    Require(BluetoothProvider.ParseBattery(5, BitConverter.GetBytes((ushort)50), 2) == 50, "UINT16 rejected");
    Require(BluetoothProvider.ParseBattery(6, BitConverter.GetBytes(50), 4) == 50, "INT32 rejected");
    Require(BluetoothProvider.ParseBattery(7, BitConverter.GetBytes(50u), 4) == 50, "UINT32 rejected");
    Require(BluetoothProvider.ParseBattery(6, BitConverter.GetBytes(-1), 4) is null, "Negative reading accepted");
});
Check("Truncated and mismatched battery properties fail safely", () =>
{
    Require(BluetoothProvider.ParseBattery(3, [], 0) is null, "Empty byte accepted");
    Require(BluetoothProvider.ParseBattery(7, [50], 4) is null, "Oversized native length accepted");
    Require(BluetoothProvider.ParseBattery(7, [50], 1) is null, "Truncated UINT32 accepted");
    Require(BluetoothProvider.ParseBattery(18, Property("50"), 6) is null, "String battery accepted");
});
Check("Address hex is case insensitive", () =>
    Require(BluetoothProvider.ParseAddressString("020000aabbcc") == jbl, "Lowercase hex rejected"));
Check("Address rejects altered formats, empty, zero and invalid hex", () =>
{
    foreach (var value in new[] { "", "000000000000", "02:00:00:AA:BB:CC", "02-00-00-AA-BB-CC",
        "020000AABBC", "020000AABBCC0", " 020000AABBCC", "020000AABBCC ", "020000AABBCZ", "020000AABBC\0" })
        Require(BluetoothProvider.ParseAddressString(value) is null, "Malformed address accepted: " + value);
});
Check("Address property must be a UTF16 string with exact length and terminator", () =>
{
    var bytes = Property("020000AABBCC");
    Require(BluetoothProvider.ParseAddressProperty(7, bytes, 26) is null, "Incorrect property type accepted");
    Require(BluetoothProvider.ParseAddressProperty(18, bytes, 24) is null, "Unterminated address accepted");
    Require(BluetoothProvider.ParseAddressProperty(18, bytes[..24], 26) is null, "Length exceeding buffer accepted");
    bytes[24] = 1;
    Require(BluetoothProvider.ParseAddressProperty(18, bytes, 26) is null, "Incorrect terminator accepted");
    Require(BluetoothProvider.ParseAddressProperty(18, Property("0200\00AABBCC"), 26) is null, "Embedded null accepted");
});
Check("Remote DEV address fallback remains supported", () =>
{
    Require(BluetoothProvider.ParseAddress(remote) == jbl, "Remote ancestor fallback failed");
    Require(BluetoothProvider.ParseAddress(remote.ToLowerInvariant()) == jbl, "Lowercase fallback failed");
});
Check("Unrelated service IDs and malformed DEV IDs are never remote devices", () =>
{
    foreach (var value in new[] { hfpService, radioParent, @"HID\DEV_020000AABBCC\1",
        @"BTHENUM\DEV_020000AABBCCEXTRA\1", @"BTHENUM\DEV_020000AABBCZ\1" })
        Require(BluetoothProvider.ParseAddress(value) is null, "Nonremote node accepted: " + value);
});
Check("Same-name devices with different MACs retain separate readings", () =>
{
    var devices = new[] { (Name: "JBL TUNE760NC", Address: "020000AABBCC", Battery: 50),
        (Name: "JBL TUNE760NC", Address: "020000AABBCD", Battery: 80) };
    var values = new Dictionary<ulong, HashSet<int>>();
    var connected = new HashSet<ulong> { jbl, second };
    foreach (var device in devices)
        BluetoothProvider.AddBatteryValue(values, connected,
            BluetoothProvider.ParseAddressProperty(18, Property(device.Address), 26), device.Battery);
    Require(values.Count == 2 && values[jbl].SetEquals([50]) && values[second].SetEquals([80]),
        "Identical names caused a cross-device association");
});
Check("Disconnected and unknown addresses cannot contribute current readings", () =>
{
    var values = new Dictionary<ulong, HashSet<int>>();
    BluetoothProvider.AddBatteryValue(values, new HashSet<ulong> { second }, jbl, 50);
    BluetoothProvider.AddBatteryValue(values, new HashSet<ulong> { second }, null, 50);
    Require(values.Count == 0, "An offline or unidentified service supplied a live battery");
});
Check("Duplicate reports collapse and conflicting reports remain detectable", () =>
{
    var values = new Dictionary<ulong, HashSet<int>>();
    var connected = new HashSet<ulong> { jbl };
    BluetoothProvider.AddBatteryValue(values, connected, jbl, 50);
    BluetoothProvider.AddBatteryValue(values, connected, jbl, 50);
    Require(values[jbl].Count == 1, "Duplicate report became a false conflict");
    BluetoothProvider.AddBatteryValue(values, connected, jbl, 60);
    Require(values[jbl].Count == 2, "Conflicting reports silently overwrote each other");
});
Check("Missing and invalid readings never become zero", () =>
{
    var values = new Dictionary<ulong, HashSet<int>>();
    var connected = new HashSet<ulong> { jbl };
    foreach (int? battery in new int?[] { null, -1, 101 })
        BluetoothProvider.AddBatteryValue(values, connected, jbl, battery);
    Require(values.Count == 0, "Unavailable property invented a battery percentage");
});

var currentCheck = "test runner";
try
{
    if (args.Contains("--self-test-failure", StringComparer.Ordinal))
    {
        currentCheck = "intentional synthetic failure";
        throw new InvalidOperationException("Requested by --self-test-failure; no hardware was accessed.");
    }
    foreach (var (name, run) in checks)
    {
        currentCheck = name;
        run();
        Console.WriteLine("PASS " + name);
    }
    Console.WriteLine($"Bluetooth checks passed: {checks.Count}");
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine($"FAIL {currentCheck}: {error}");
    return 1;
}
