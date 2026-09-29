namespace Cbpl.Rmms.Collector.Protocol;

public enum RollEndReason : ushort
{
    Splice = 1,
    Reset = 2
}

public sealed record RollEvent(
    uint EventId,
    uint TimestampUtc,
    ushort UnwindNumber,
    ushort ShiftNumber,
    RollEndReason EndReason,
    uint LengthMm,
    ushort[] RawWords)
{
    private const uint MinPlausibleTimestampUtc = 1_577_836_800; // 2020-01-01
    private const uint MaxPlausibleTimestampUtc = 4_102_444_800; // 2100-01-01

    public bool TimestampValid =>
        TimestampUtc >= MinPlausibleTimestampUtc && TimestampUtc < MaxPlausibleTimestampUtc;

    public string RawPacketHex => string.Join(' ', RawWords.Select(word => word.ToString("X4")));
}

