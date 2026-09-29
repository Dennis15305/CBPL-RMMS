using Cbpl.Rmms.Collector.Protocol;
using Xunit;

namespace Cbpl.Rmms.Collector.Tests;

public sealed class ProtocolTests
{
    private static readonly ushort[] TestVector =
    [
        0xCB01, 0x0002, 0x0000, 0x0011,
        0x6A8E, 0xC154, 0x0003, 0x0002,
        0x0001, 0x0001, 0xE208, 0x41AE
    ];

    [Fact]
    public void OfficialProtocolV2VectorIsDecodedExactly()
    {
        Assert.Equal((ushort)0x41AE, EventProtocol.CalculateCrc(TestVector.AsSpan(0, 11)));
        RollEvent rollEvent = EventProtocol.Parse(TestVector);
        Assert.Equal((uint)17, rollEvent.EventId);
        Assert.Equal((uint)1_787_740_500, rollEvent.TimestampUtc);
        Assert.Equal((ushort)3, rollEvent.UnwindNumber);
        Assert.Equal((ushort)2, rollEvent.ShiftNumber);
        Assert.Equal(RollEndReason.Splice, rollEvent.EndReason);
        Assert.Equal((uint)123_400, rollEvent.LengthMm);
        Assert.True(rollEvent.TimestampValid);
    }

    [Fact]
    public void DamagedPacketIsRejectedByCrc()
    {
        ushort[] damaged = (ushort[])TestVector.Clone();
        damaged[10] ^= 1;
        Assert.Throws<ProtocolException>(() => EventProtocol.Parse(damaged));
    }

    [Fact]
    public void AckUsesHighWordThenLowWord()
    {
        Assert.Equal([0x1234, 0x5678], EventProtocol.SplitUInt32(0x12345678));
    }

    [Fact]
    public void LegacyRtcDateIsPreservedButMarkedInvalid()
    {
        var rollEvent = new RollEvent(
            1,
            1_160_723_838,
            1,
            1,
            RollEndReason.Reset,
            109_741,
            TestVector);

        Assert.False(rollEvent.TimestampValid);
    }

    [Fact]
    public void OperationalSnapshotDecodesFiveRollsAndStatus()
    {
        var words = new ushort[OperationalStateProtocol.WordCount];
        words[0] = OperationalStateProtocol.Signature;
        words[1] = OperationalStateProtocol.Version;
        words[2] = 321;
        words[7] = 4;
        words[10] = 0;
        words[11] = 123;
        words[20] = 0;
        words[21] = 456;
        words[30] = 0b1_1111;
        words[42] = 25;
        words[54] = 0;
        words[55] = 7;

        PlcOperationalSnapshot snapshot = OperationalStateProtocol.Parse(words);

        Assert.Equal(321L, snapshot.PlcHeartbeat);
        Assert.Equal(4, snapshot.QueueCount);
        Assert.Equal(7L, snapshot.QueueOverflowCount);
        Assert.Equal(5, snapshot.Rolls.Count);

        RollLiveSnapshot first = snapshot.Rolls[0];
        Assert.Equal((byte)1, first.RollNumber);
        Assert.True(first.IsConfigured);
        Assert.True(first.IsActive);
        Assert.Equal(12_300L, first.CurrentLengthMm);
        Assert.Equal(45_600L, first.PreviousLengthMm);
        Assert.Equal(2_500L, first.SpeedMmPerMinute);
        Assert.True(first.SpliceSignal);
        Assert.True(first.ResetSignal);
        Assert.True(first.InputError);
    }
}
