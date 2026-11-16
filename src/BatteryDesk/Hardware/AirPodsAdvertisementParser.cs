using System;
using System.Linq;

namespace BatteryDesk.Hardware;

internal sealed record AirPodsAdvertisementReading(
    int ModelId, int? Left, int? Right, int? Case,
    bool LeftCharging, bool RightCharging, bool CaseCharging,
    bool SingleBattery, short Rssi, DateTimeOffset SeenUtc)
{
    public int? SummaryPercent
    {
        get
        {
            if (SingleBattery) return Left;
            var earbuds = new[] { Left, Right }.Where(value => value.HasValue).Select(value => value!.Value).ToArray();
            return earbuds.Length > 0 ? earbuds.Min() : Case;
        }
    }

    public bool? SummaryCharging
    {
        get
        {
            if (SingleBattery) return Left.HasValue ? LeftCharging : null;
            if (Left.HasValue && (!Right.HasValue || Left.Value <= Right.Value)) return LeftCharging;
            if (Right.HasValue) return RightCharging;
            return Case.HasValue ? CaseCharging : null;
        }
    }

    public string Signature => string.Join(":", ModelId, Left, Right, Case,
        LeftCharging ? 1 : 0, RightCharging ? 1 : 0, CaseCharging ? 1 : 0, SingleBattery ? 1 : 0);
}

internal static class AirPodsAdvertisementParser
{
    public const ushort AppleCompanyId = 0x004C;
    private const byte ProximityPairingType = 0x07;
    private const byte ProximityPairingLength = 25;

    public static AirPodsAdvertisementReading? Parse(byte[] data, short rssi, DateTimeOffset seenUtc)
    {
        if (data.Length < 27 || data[0] != ProximityPairingType || data[1] != ProximityPairingLength || rssi < -75)
            return null;

        var model = (data[3] << 8) | data[4];
        var single = model is 0x0A20 or 0x1F20;
        var flip = (data[5] & 0x20) == 0;
        var first = data[6] >> 4;
        var second = data[6] & 0x0F;
        var caseLevel = data[7] & 0x0F;
        var chargingFlags = data[7] >> 4;

        int? left;
        int? right;
        bool leftCharging;
        bool rightCharging;
        bool caseCharging;
        if (single)
        {
            left = DecodeLevel(second);
            right = null;
            leftCharging = (chargingFlags & 0x1) != 0;
            rightCharging = false;
            caseCharging = false;
        }
        else
        {
            left = DecodeLevel(flip ? first : second);
            right = DecodeLevel(flip ? second : first);
            leftCharging = (chargingFlags & (flip ? 0x2 : 0x1)) != 0;
            rightCharging = (chargingFlags & (flip ? 0x1 : 0x2)) != 0;
            caseCharging = (chargingFlags & 0x4) != 0;
        }

        var caseBattery = single ? null : DecodeLevel(caseLevel);
        if (left is null && right is null && caseBattery is null) return null;

        return new(model, left, right, caseBattery, leftCharging, rightCharging, caseCharging, single, rssi, seenUtc);
    }

    public static int? DecodeLevel(int nibble) => nibble switch
    {
        >= 0 and <= 10 => nibble * 10,
        _ => null
    };
}
