using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace BatteryDesk.Hardware;

public static class XInputProvider
{
    private const uint DeviceNotConnected = 1167;
    private const string Provider = "Windows XInput · Four battery levels";

    public static IReadOnlyList<DeviceObservation> Read(IReadOnlyList<HidEndpoint> endpoints, bool allowSlotMatch = true)
    {
        var candidates = Candidates(endpoints);
        if (candidates.Length == 0) return [];
        var slots = new List<SlotReading>();
        try
        {
            for (uint index = 0; index < 4; index++)
            {
                var stateResult = XInputGetState(index, out _);
                var battery = new NativeBattery();
                var batteryResult = stateResult == 0
                    ? XInputGetBatteryInformation(index, 0, out battery)
                    : stateResult;
                slots.Add(new(index, stateResult, batteryResult, battery.Type, battery.Level));
            }
        }
        catch (Exception ex) when (IsUnavailable(ex))
        {
            slots.Clear();
        }
        return BuildObservations(candidates, slots, ResolveIdentity, allowSlotMatch);
    }

    // A controller can switch on/off while its USB receiver remains present. Only query
    // connection here; the normal refresh interval controls battery queries.
    public static string GetConnectionSignature()
    {
        try
        {
            var results = new uint[4];
            for (uint index = 0; index < 4; index++) results[index] = XInputGetState(index, out _);
            return string.Join("|", results);
        }
        catch (Exception ex) when (IsUnavailable(ex)) { return "xinput-unavailable"; }
    }

    internal sealed record SlotReading(uint Index, uint StateResult, uint BatteryResult, byte BatteryType, byte Level);
    internal sealed record ControllerIdentity(string Id, bool XboxReceiver, Guid? ContainerId = null);

    // Kept separate from native queries so ambiguous identity and stale-data cases can
    // be checked without reading hardware or storing controller button input.
    internal static IReadOnlyList<DeviceObservation> BuildObservations(IReadOnlyList<HidEndpoint> endpoints,
        IReadOnlyList<SlotReading> slots, Func<HidEndpoint, ControllerIdentity> resolveIdentity, bool allowSlotMatch = true)
    {
        var candidates = Candidates(endpoints);
        var connected = slots.Where(s => s.StateResult == 0).ToArray();
        var complete = slots.Count == 4 && slots.Select(s => s.Index).Distinct().Count() == 4
            && slots.All(s => s.Index < 4);
        var failed = !complete || slots.Any(s => s.StateResult != 0 && s.StateResult != DeviceNotConnected);
        var output = new List<DeviceObservation>();
        foreach (var endpoint in candidates)
        {
            var identity = resolveIdentity(endpoint);
            var name = endpoint.VendorId == 0x045e && endpoint.Product.Contains("Xbox", StringComparison.OrdinalIgnoreCase)
                ? (identity.XboxReceiver ? "Xbox Wireless Controller" : "Xbox Controller")
                : (string.IsNullOrWhiteSpace(endpoint.Product) ? "XInput Controller" : endpoint.Product.Trim());
            if (candidates.Length > 1) name += " · " + identity.Id[^6..];
            var transport = identity.XboxReceiver ? "Xbox Wireless Adapter" : "XInput";
            var connection = LinkState.Unknown;
            BatteryLevel? level = null;
            string message;
            if (failed)
                message = !complete ? "The system controller interface is unavailable" : "Could not read the controller connection state; refresh again later";
            else if (connected.Length == 0)
            {
                connection = LinkState.Disconnected;
                message = "Controller disconnected; showing the last battery reading as cached";
            }
            else if (!allowSlotMatch || connected.Length != 1 || candidates.Length != 1)
                message = "Multiple controllers detected; battery readings cannot be matched reliably. Connect one controller and refresh";
            else
            {
                connection = LinkState.Connected;
                var slot = connected[0];
                if (!identity.XboxReceiver && slot.BatteryResult == 0)
                    transport = slot.BatteryType switch
                    {
                        1 => "USB wired",
                        2 or 3 => "Wireless (XInput)",
                        _ => "XInput"
                    };
                if (slot.BatteryResult != 0)
                    message = "Controller connected; battery query failed. Keeping the last valid reading";
                else if (slot.BatteryType is 2 or 3 && slot.Level <= 3)
                {
                    level = (BatteryLevel)slot.Level;
                    message = "Controller responded; the device reports four battery levels, not a percentage";
                }
                else if (slot.BatteryType == 1)
                    message = "Controller connected; the system reports wired power without a battery reading";
                else
                    message = "Controller connected; no valid battery reading returned. Keeping the last valid reading";
            }
            output.Add(new(identity.Id, name, DeviceKind.Controller, transport, connection, null, null,
                message, Provider, CurrentBatteryLevel: level));
        }
        // More than one HID collection may point at one physical controller. Keep
        // its conservative ambiguous result once, so the state cache has unique IDs.
        return output.DistinctBy(o => o.Id).ToArray();
    }

    private static HidEndpoint[] Candidates(IReadOnlyList<HidEndpoint> endpoints) => endpoints
        .Where(e => e.UsagePage == 1 && e.Usage is 4 or 5 && e.Path.Contains("&ig_", StringComparison.OrdinalIgnoreCase))
        .DistinctBy(e => e.Path, StringComparer.OrdinalIgnoreCase)
        .OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase).ToArray();

    internal static ControllerIdentity CreateIdentity(HidEndpoint endpoint, string? controllerParent, string? receiverParent)
    {
        var parent = Normalize(controllerParent ?? "");
        // Never hash a shared receiver or a slot as if it identified a physical pad.
        var controllerPrefix = $"USB\\VID_{endpoint.VendorId:X4}&PID_{endpoint.ProductId:X4}";
        var source = HasDevicePrefix(parent, controllerPrefix) && !IsXboxReceiver(parent)
            ? parent : Normalize(endpoint.Path);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant();
        return new("xinput:" + hash, IsXboxReceiver(Normalize(receiverParent ?? "")));
    }

    internal static string? InstanceIdFromPath(string path)
    {
        if (!path.StartsWith(@"\\?\", StringComparison.Ordinal)) return null;
        var end = path.IndexOf("#{", StringComparison.Ordinal);
        if (end < 4) return null;
        var instanceId = path[4..end].Replace('#', '\\');
        return instanceId.StartsWith("HID\\", StringComparison.OrdinalIgnoreCase) ? instanceId : null;
    }

    internal static ControllerIdentity ResolveIdentity(HidEndpoint endpoint)
    {
        string? parentId = null, receiverId = null;
        Guid? parentContainer = null, receiverContainer = null;
        try
        {
            var instanceId = InstanceIdFromPath(endpoint.Path);
            if (instanceId is not null && CM_Locate_DevNode(out var device, instanceId, 0) == 0
                && CM_Get_Parent(out var parent, device, 0) == 0)
            {
                parentId = DeviceId(parent);
                parentContainer = ContainerId(parent);
                if (CM_Get_Parent(out var receiver, parent, 0) == 0)
                {
                    receiverId = DeviceId(receiver);
                    receiverContainer = ContainerId(receiver);
                }
            }
        }
        catch (Exception ex) when (IsUnavailable(ex)) { }
        var identity = CreateIdentity(endpoint, parentId, receiverId);
        var controllerPrefix = $"USB\\VID_{endpoint.VendorId:X4}&PID_{endpoint.ProductId:X4}";
        return HasDevicePrefix(Normalize(parentId ?? ""), controllerPrefix) && !IsXboxReceiver(Normalize(parentId ?? ""))
            && parentContainer is not null && parentContainer != receiverContainer
            ? identity with { ContainerId = parentContainer } : identity;
    }

    private static Guid? ContainerId(uint device)
    {
        var key = new PropertyKey { Format = new Guid("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"), Id = 2 };
        var buffer = new byte[16];
        uint size = (uint)buffer.Length;
        if (CM_Get_DevNode_Property(device, ref key, out var type, buffer, ref size, 0) != 0 || type != 13 || size != 16)
            return null;
        var value = new Guid(buffer);
        return value == Guid.Empty ? null : value;
    }

    private static string? DeviceId(uint device)
    {
        if (CM_Get_Device_ID_Size(out var length, device, 0) != 0 || length == 0 || length > 4096) return null;
        var buffer = new StringBuilder((int)length + 1);
        return CM_Get_Device_ID(device, buffer, (uint)buffer.Capacity, 0) == 0 ? buffer.ToString() : null;
    }

    private static string Normalize(string value) => value.Trim().ToUpperInvariant();
    private static bool HasDevicePrefix(string value, string prefix) => value.StartsWith(prefix, StringComparison.Ordinal)
        && value.Length > prefix.Length && value[prefix.Length] is '\\' or '&';
    // This receiver topology was verified on the user's machine. Other transports
    // remain generic until their ancestry is known, rather than guessing from flags.
    private static bool IsXboxReceiver(string value) => HasDevicePrefix(value, "USB\\VID_045E&PID_02E6");
    private static bool IsUnavailable(Exception ex) => ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException;

    [StructLayout(LayoutKind.Sequential)] private struct NativeGamepad
    {
        public ushort Buttons;
        public byte LeftTrigger, RightTrigger;
        public short LeftX, LeftY, RightX, RightY;
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeState { public uint PacketNumber; public NativeGamepad Gamepad; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeBattery { public byte Type, Level; }
    [StructLayout(LayoutKind.Sequential)] private struct PropertyKey { public Guid Format; public uint Id; }
    [DllImport("xinput1_4.dll", ExactSpelling = true)] private static extern uint XInputGetState(uint index, out NativeState state);
    [DllImport("xinput1_4.dll", ExactSpelling = true)] private static extern uint XInputGetBatteryInformation(uint index, byte deviceType, out NativeBattery battery);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Locate_DevNodeW", ExactSpelling = true)] private static extern uint CM_Locate_DevNode(out uint device, string instanceId, uint flags);
    [DllImport("cfgmgr32.dll", ExactSpelling = true)] private static extern uint CM_Get_Parent(out uint parent, uint device, uint flags);
    [DllImport("cfgmgr32.dll", ExactSpelling = true)] private static extern uint CM_Get_Device_ID_Size(out uint length, uint device, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Get_Device_IDW", ExactSpelling = true)] private static extern uint CM_Get_Device_ID(uint device, StringBuilder buffer, uint length, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Get_DevNode_PropertyW", ExactSpelling = true)] private static extern uint CM_Get_DevNode_Property(uint device, ref PropertyKey key, out uint type, byte[] buffer, ref uint size, uint flags);
}
