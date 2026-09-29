namespace Cbpl.Rmms.Collector.Protocol;

public sealed record StopJournalEvent(
    uint EventId,
    uint TimestampUtc,
    ushort Kind,
    ushort StateCode,
    ushort ShiftNumber,
    uint OrderNumber,
    uint OrderLengthDm,
    uint OrderAreaX100,
    ushort SpeedDmPerMinute,
    ushort OperatorNumber,
    ushort ChangeoverSetpointTenthsMin,
    ushort AlarmBits,
    ushort BreakPoint,
    ushort BreakSide,
    ushort BreakReason,
    uint ShiftLengthDm,
    uint ShiftAreaX100,
    ushort[] RawWords);

public sealed record StopJournalSnapshot(
    StopJournalEvent? Event,
    ushort QueueCount,
    ushort QueueStatus,
    uint OverflowCount,
    ushort PlcHeartbeat);

public static class StopJournalProtocol
{
    public const ushort Signature = 0x534A;
    public const ushort Version = 1;
    public const ushort AckCommand = 0xA55B;

    public const ushort BaseAddress = 2400;
    public const ushort SnapshotWords = 36;
    public const int EventDataWords = 26;
    public const int EventReadyIndex = 26;
    public const int QueueCountIndex = 27;
    public const int QueueStatusIndex = 28;
    public const int OverflowHighIndex = 33;
    public const int HeartbeatIndex = 35;

    public const ushort AckEventIdAddress = 2429;
    public const ushort AckCommandAddress = 2431;
    public const ushort LastAckResultAddress = 2432;
    public const ushort AckVerificationWords = 33;

    public static StopJournalSnapshot ParseSnapshot(ReadOnlySpan<ushort> words)
    {
        if (words.Length != SnapshotWords)
            throw new ProtocolException(
                $"Stop journal snapshot must contain {SnapshotWords} words, got {words.Length}.");
        if (words[0] != Signature)
            throw new ProtocolException(
                $"Invalid stop journal signature 0x{words[0]:X4}; expected 0x{Signature:X4}.");
        if (words[1] != Version)
            throw new ProtocolException(
                $"Unsupported stop journal version {words[1]}; expected {Version}.");

        ushort ready = words[EventReadyIndex];
        ushort count = words[QueueCountIndex];
        ushort status = words[QueueStatusIndex];
        if (ready is not (0 or 1))
            throw new ProtocolException($"Invalid stop journal EventReady value {ready}.");
        if ((ready == 1) != (count > 0))
            throw new ProtocolException(
                $"Inconsistent stop journal ready/count values: ready={ready}, count={count}.");
        if (count > 128)
            throw new ProtocolException($"Invalid stop journal queue count {count}.");
        if (status > 4)
            throw new ProtocolException($"Invalid stop journal queue status {status}.");

        StopJournalEvent? item = ready == 1 ? ParseEvent(words[..EventDataWords]) : null;
        return new StopJournalSnapshot(
            item,
            count,
            status,
            EventProtocol.JoinUInt32(words[OverflowHighIndex], words[OverflowHighIndex + 1]),
            words[HeartbeatIndex]);
    }

    public static StopJournalEvent ParseEvent(ReadOnlySpan<ushort> words)
    {
        if (words.Length != EventDataWords)
            throw new ProtocolException(
                $"Stop journal event must contain {EventDataWords} words, got {words.Length}.");

        uint eventId = EventProtocol.JoinUInt32(words[2], words[3]);
        ushort kind = words[6];
        ushort state = words[7];
        ushort shift = words[8];
        if (eventId == 0)
            throw new ProtocolException("Stop journal EventId 0 is reserved.");
        if (kind is < 1 or > 3)
            throw new ProtocolException($"Invalid stop journal event kind {kind}.");
        if (shift > 2)
            throw new ProtocolException($"Invalid stop journal shift number {shift}.");
        if (!IsKnownState(state))
            throw new ProtocolException($"Invalid stop journal state code {state}.");
        if (kind == 2 && state != 32768)
            throw new ProtocolException("Shift-boundary event must use state code 32768.");
        if (kind != 2 && state == 32768)
            throw new ProtocolException("State code 32768 is reserved for shift-boundary events.");

        return new StopJournalEvent(
            eventId,
            EventProtocol.JoinUInt32(words[4], words[5]),
            kind,
            state,
            shift,
            EventProtocol.JoinUInt32(words[9], words[10]),
            EventProtocol.JoinUInt32(words[11], words[12]),
            EventProtocol.JoinUInt32(words[13], words[14]),
            words[15],
            words[16],
            words[17],
            words[18],
            words[19],
            words[20],
            words[21],
            EventProtocol.JoinUInt32(words[22], words[23]),
            EventProtocol.JoinUInt32(words[24], words[25]),
            words.ToArray());
    }

    private static bool IsKnownState(ushort value) => value is
        1 or 2 or 8 or 16 or 32 or 64 or 128 or 256 or 512 or 1024 or 2048 or 32768;
}
