using Cbpl.Rmms.Collector.Protocol;
using Xunit;

namespace Cbpl.Rmms.Collector.Tests;

public sealed class StopJournalProtocolTests
{
    [Fact]
    public void SnapshotDecodesProductionLineEventExactly()
    {
        ushort[] words = TestPacket.CreateSnapshot();

        StopJournalSnapshot snapshot = StopJournalProtocol.ParseSnapshot(words);

        StopJournalEvent item = Assert.IsType<StopJournalEvent>(snapshot.Event);
        Assert.Equal((uint)42, item.EventId);
        Assert.Equal((ushort)8, item.StateCode);
        Assert.Equal((ushort)1, item.ShiftNumber);
        Assert.Equal((uint)1234, item.OrderNumber);
        Assert.Equal((uint)456, item.OrderLengthDm);
        Assert.Equal((uint)789, item.OrderAreaX100);
        Assert.Equal((ushort)123, item.SpeedDmPerMinute);
        Assert.Equal((uint)1000, item.ShiftLengthDm);
        Assert.Equal((uint)2000, item.ShiftAreaX100);
        Assert.Equal((ushort)1, snapshot.QueueCount);
        Assert.Equal((ushort)9, snapshot.PlcHeartbeat);
    }

    [Fact]
    public void InconsistentReadyAndQueueCountIsRejected()
    {
        ushort[] words = TestPacket.CreateSnapshot();
        words[StopJournalProtocol.QueueCountIndex] = 0;

        Assert.Throws<ProtocolException>(
            () => StopJournalProtocol.ParseSnapshot(words));
    }

    internal static class TestPacket
    {
        public static ushort[] CreateSnapshot()
        {
            var words = new ushort[StopJournalProtocol.SnapshotWords];
            words[0] = StopJournalProtocol.Signature;
            words[1] = StopJournalProtocol.Version;
            words[2] = 0;
            words[3] = 42;
            words[4] = 0x6A8E;
            words[5] = 0xC154;
            words[6] = 1;
            words[7] = 8;
            words[8] = 1;
            words[9] = 0;
            words[10] = 1234;
            words[11] = 0;
            words[12] = 456;
            words[13] = 0;
            words[14] = 789;
            words[15] = 123;
            words[16] = 7;
            words[17] = 300;
            words[18] = 2;
            words[22] = 0;
            words[23] = 1000;
            words[24] = 0;
            words[25] = 2000;
            words[StopJournalProtocol.EventReadyIndex] = 1;
            words[StopJournalProtocol.QueueCountIndex] = 1;
            words[StopJournalProtocol.QueueStatusIndex] = 0;
            words[StopJournalProtocol.HeartbeatIndex] = 9;
            return words;
        }
    }
}
