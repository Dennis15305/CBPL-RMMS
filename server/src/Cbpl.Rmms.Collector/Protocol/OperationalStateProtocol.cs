namespace Cbpl.Rmms.Collector.Protocol;

public sealed record RollLiveSnapshot(
    byte RollNumber,
    bool IsConfigured,
    bool IsActive,
    long CurrentLengthMm,
    long PreviousLengthMm,
    long SpeedMmPerMinute,
    bool SpliceSignal,
    bool ResetSignal,
    bool InputError);

public sealed record PlcOperationalSnapshot(
    long PlcHeartbeat,
    int QueueCount,
    long QueueOverflowCount,
    IReadOnlyList<RollLiveSnapshot> Rolls);

/// <summary>
/// Decodes holding registers 2100..2157 from the existing PLC live-data map.
/// Length and speed values in this block use 0.1 metre units.
/// </summary>
public static class OperationalStateProtocol
{
    public const ushort BaseAddress = 2100;
    public const ushort WordCount = 58;
    public const ushort Signature = 0x484D;
    public const ushort Version = 2;
    public const int RollCount = 5;

    private const int CurrentLengthIndex = 10;
    private const int PreviousLengthIndex = 20;
    private const int RollStatusIndex = 30;
    private const int SpeedIndex = 42;
    private const int QueueCountIndex = 7;
    private const int OverflowCountIndex = 54;

    public static PlcOperationalSnapshot Parse(ReadOnlySpan<ushort> words)
    {
        if (words.Length != WordCount)
            throw new ProtocolException(
                $"Operational snapshot must contain {WordCount} words, got {words.Length}.");
        if (words[0] != Signature)
            throw new ProtocolException(
                $"Invalid operational signature 0x{words[0]:X4}; expected 0x{Signature:X4}.");
        if (words[1] != Version)
            throw new ProtocolException(
                $"Unsupported operational protocol version {words[1]}; expected {Version}.");

        long lineSpeedMmPerMinute = checked((long)words[SpeedIndex] * 100L);
        var rolls = new RollLiveSnapshot[RollCount];

        for (int index = 0; index < RollCount; index++)
        {
            uint currentX10 = EventProtocol.JoinUInt32(
                words[CurrentLengthIndex + index * 2],
                words[CurrentLengthIndex + index * 2 + 1]);
            uint previousX10 = EventProtocol.JoinUInt32(
                words[PreviousLengthIndex + index * 2],
                words[PreviousLengthIndex + index * 2 + 1]);
            ushort status = words[RollStatusIndex + index];

            long currentLengthMm = checked((long)currentX10 * 100L);
            bool isActive = (status & (1 << 1)) != 0;

            rolls[index] = new RollLiveSnapshot(
                checked((byte)(index + 1)),
                (status & (1 << 0)) != 0,
                isActive,
                currentLengthMm,
                checked((long)previousX10 * 100L),
                isActive ? lineSpeedMmPerMinute : 0,
                (status & (1 << 2)) != 0,
                (status & (1 << 3)) != 0,
                (status & (1 << 4)) != 0);
        }

        return new PlcOperationalSnapshot(
            words[2],
            words[QueueCountIndex],
            EventProtocol.JoinUInt32(
                words[OverflowCountIndex], words[OverflowCountIndex + 1]),
            rolls);
    }
}