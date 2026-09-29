using Cbpl.Rmms.Collector.Configuration;
using Cbpl.Rmms.Collector.Diagnostics;
using Cbpl.Rmms.Collector.Modbus;
using Cbpl.Rmms.Collector.Protocol;
using Xunit;

namespace Cbpl.Rmms.Collector.Tests;

public sealed class StopJournalEngineTests
{
    [Fact]
    public async Task SqlStorageCompletesBeforeFinalAckCommand()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"cbpl-stop-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var sequence = new List<string>();
            var modbus = new FakeModbus(sequence);
            var sink = new FakeSink(sequence);
            var log = new AppLog(new LoggingOptions
            {
                Level = "Error",
                FilePath = Path.Combine(directory, "test.log")
            });
            var engine = new StopJournalEngine(
                new PlcOptions { EnableWrites = true, AckVerifyTimeoutSeconds = 0.2 },
                modbus, sink, log);

            StopJournalCycleResult result =
                await engine.CollectOnceAsync(CancellationToken.None);

            Assert.Equal(StopJournalCycleResult.Acknowledged, result);
            Assert.Equal(
                ["read", "read", "sql", "ack-id", "ack-command", "verify"],
                sequence);
            Assert.Equal((uint)42, Assert.Single(sink.Items).EventId);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task FailedSqlWriteNeverAcknowledgesPlc()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"cbpl-stop-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var sequence = new List<string>();
            var modbus = new FakeModbus(sequence);
            var log = new AppLog(new LoggingOptions
            {
                Level = "Error",
                FilePath = Path.Combine(directory, "test.log")
            });
            var engine = new StopJournalEngine(
                new PlcOptions { EnableWrites = true },
                modbus, new FailingSink(), log);

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => engine.CollectOnceAsync(CancellationToken.None));

            Assert.Equal(["read", "read"], sequence);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class FakeSink(List<string> sequence) : IStopJournalSink
    {
        public List<StopJournalEvent> Items { get; } = [];

        public Task<bool> StoreAsync(
            StopJournalEvent item,
            CancellationToken cancellationToken)
        {
            sequence.Add("sql");
            Items.Add(item);
            return Task.FromResult(true);
        }
    }

    private sealed class FailingSink : IStopJournalSink
    {
        public Task<bool> StoreAsync(
            StopJournalEvent item,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("SQL unavailable");
    }

    private sealed class FakeModbus(List<string> sequence) : IModbusClient
    {
        private int _snapshotReads;

        public Task<ushort[]> ReadHoldingRegistersAsync(
            ushort address,
            ushort count,
            CancellationToken cancellationToken)
        {
            if (count == StopJournalProtocol.SnapshotWords)
            {
                _snapshotReads++;
                sequence.Add("read");
                return Task.FromResult(
                    StopJournalProtocolTests.TestPacket.CreateSnapshot());
            }
            if (count == StopJournalProtocol.AckVerificationWords)
            {
                sequence.Add("verify");
                var words = new ushort[StopJournalProtocol.AckVerificationWords];
                words[StopJournalProtocol.EventReadyIndex] = 0;
                words[StopJournalProtocol.AckCommandAddress - StopJournalProtocol.BaseAddress] = 0;
                words[StopJournalProtocol.LastAckResultAddress - StopJournalProtocol.BaseAddress] = 1;
                return Task.FromResult(words);
            }
            throw new InvalidOperationException(
                $"Unexpected read address/count {address}/{count}; reads={_snapshotReads}.");
        }

        public Task WriteSingleRegisterAsync(
            ushort address,
            ushort value,
            CancellationToken cancellationToken)
        {
            Assert.Equal(StopJournalProtocol.AckCommandAddress, address);
            Assert.Equal(StopJournalProtocol.AckCommand, value);
            sequence.Add("ack-command");
            return Task.CompletedTask;
        }

        public Task WriteMultipleRegistersAsync(
            ushort address,
            IReadOnlyList<ushort> values,
            CancellationToken cancellationToken)
        {
            Assert.Equal(StopJournalProtocol.AckEventIdAddress, address);
            Assert.Equal([0, 42], values);
            sequence.Add("ack-id");
            return Task.CompletedTask;
        }
    }
}
