namespace Cbpl.Rmms.Collector.Protocol;

public static class EventProtocol
{
    public const ushort Signature = 0xCB01;
    public const ushort Version = 2;
    public const ushort AckCommand = 0xA55A;

    public const ushort EventBaseAddress = 2000;
    public const ushort EventPacketWords = 12;
    public const ushort SnapshotWords = 16;
    public const int EventReadyIndex = 12;
    public const int QueueCountIndex = 13;
    public const int QueueStatusIndex = 15;

    public const ushort AckEventIdAddress = 2016;
    public const ushort AckCommandAddress = 2018;
    public const ushort CollectorHeartbeatAddress = 2019;
    public const ushort LastAckResultAddress = 2020;

    public static RollEvent Parse(ReadOnlySpan<ushort> words)
    {
        if (words.Length != EventPacketWords)
            throw new ProtocolException($"Event packet must contain {EventPacketWords} words, got {words.Length}.");
        if (words[0] != Signature)
            throw new ProtocolException($"Invalid signature 0x{words[0]:X4}; expected 0x{Signature:X4}.");
        if (words[1] != Version)
            throw new ProtocolException($"Unsupported protocol version {words[1]}; expected {Version}.");

        ushort expectedCrc = CalculateCrc(words[..11]);
        if (words[11] != expectedCrc)
            throw new ProtocolException($"CRC mismatch: packet=0x{words[11]:X4}, calculated=0x{expectedCrc:X4}.");

        uint eventId = JoinUInt32(words[2], words[3]);
        uint timestamp = JoinUInt32(words[4], words[5]);
        ushort unwind = words[6];
        ushort shift = words[7];
        uint lengthMm = JoinUInt32(words[9], words[10]);

        if (eventId == 0)
            throw new ProtocolException("EventId 0 is reserved.");
        if (unwind is < 1 or > 5)
            throw new ProtocolException($"Invalid unwind number {unwind}.");
        if (shift > 2)
            throw new ProtocolException($"Invalid shift number {shift}.");
        if (!Enum.IsDefined(typeof(RollEndReason), words[8]))
            throw new ProtocolException($"Invalid roll-end reason {words[8]}.");

        return new RollEvent(
            eventId,
            timestamp,
            unwind,
            shift,
            (RollEndReason)words[8],
            lengthMm,
            words.ToArray());
    }

    public static ushort CalculateCrc(ReadOnlySpan<ushort> words)
    {
        ushort crc = 0xFFFF;
        foreach (ushort word in words)
        {
            crc = UpdateCrc(crc, (byte)(word >> 8));
            crc = UpdateCrc(crc, (byte)(word & 0xFF));
        }
        return crc;
    }

    private static ushort UpdateCrc(ushort crc, byte value)
    {
        crc ^= value;
        for (int bit = 0; bit < 8; bit++)
            crc = (ushort)((crc & 1) != 0 ? (crc >> 1) ^ 0xA001 : crc >> 1);
        return crc;
    }

    public static uint JoinUInt32(ushort highWord, ushort lowWord) =>
        ((uint)highWord << 16) | lowWord;

    public static ushort[] SplitUInt32(uint value) =>
        [(ushort)(value >> 16), (ushort)(value & 0xFFFF)];
}

public sealed class ProtocolException(string message) : Exception(message);
