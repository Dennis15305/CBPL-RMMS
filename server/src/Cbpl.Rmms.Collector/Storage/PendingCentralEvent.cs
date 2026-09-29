namespace Cbpl.Rmms.Collector.Storage;

public sealed record PendingCentralEvent(
    long LocalId,
    long PlcEventId,
    long PlcUnixTime,
    bool EventTimeValid,
    int RollNumber,
    int ShiftNumber,
    int EventType,
    long LengthMm,
    string ReceivedAtUtc,
    string RawPacketHex);