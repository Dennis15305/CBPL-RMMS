using Cbpl.Rmms.Collector.Configuration;
using Cbpl.Rmms.Collector.Diagnostics;
using Cbpl.Rmms.Collector.Modbus;
using Cbpl.Rmms.Collector.Protocol;
using Cbpl.Rmms.Collector.Storage;
using Xunit;

namespace Cbpl.Rmms.Collector.Tests;

public sealed class CollectorEngineTests
{
    [Fact]
    public async Task ReadOnlyModeStoresEventAndNeverWritesToPlc()
    {
        await WithEnvironmentAsync(async environment =>
        {
            var modbus = new FakeModbusClient();
            var options = new PlcOptions { EnableWrites = false };
            var engine = new CollectorEngine(options, modbus, environment.Store, environment.Log);

            CycleResult result = await engine.CollectOnceAsync(CancellationToken.None);

            Assert.Equal(CycleResult.StoredReadOnly, result);
            Assert.True(await environment.Store.HasEventAsync(17, CancellationToken.None));
            Assert.Empty(modbus.Writes);
        });
    }

    [Fact]
    public async Task EventIdWordsAreWrittenBeforeFinalAckCommand()
    {
        await WithEnvironmentAsync(async environment =>
        {
            var modbus = new FakeModbusClient();
            var options = new PlcOptions
            {
                EnableWrites = true,
                AckVerifyTimeoutSeconds = 0.2
            };
            var engine = new CollectorEngine(options, modbus, environment.Store, environment.Log);

            CycleResult result = await engine.CollectOnceAsync(CancellationToken.None);

            Assert.Equal(CycleResult.Acknowledged, result);
            ModbusWrite[] ackWrites = modbus.Writes
                .Where(write => write.Address != EventProtocol.CollectorHeartbeatAddress)
                .ToArray();
            Assert.Equal(2, ackWrites.Length);
            Assert.Equal("multiple", ackWrites[0].Kind);
            Assert.Equal(EventProtocol.AckEventIdAddress, ackWrites[0].Address);
            Assert.Equal([0, 17], ackWrites[0].Values);
            Assert.Equal("single", ackWrites[1].Kind);
            Assert.Equal(EventProtocol.AckCommandAddress, ackWrites[1].Address);
            Assert.Equal([EventProtocol.AckCommand], ackWrites[1].Values);
        });
    }

    private static async Task WithEnvironmentAsync(Func<TestEnvironment, Task> test)
    {
        string directory = Path.Combine(Path.GetTempPath(), $"cbpl-engine-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string schema = Path.Combine(AppContext.BaseDirectory, "schema.sql");
            var store = new EventStore(Path.Combine(directory, "events.db"), schema);
            await store.InitializeAsync(CancellationToken.None);
            var log = new AppLog(new LoggingOptions
            {
                Level = "Error",
                FilePath = Path.Combine(directory, "test.log")
            });
            await test(new TestEnvironment(store, log));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed record TestEnvironment(EventStore Store, AppLog Log);

    private sealed record ModbusWrite(string Kind, ushort Address, ushort[] Values);

    private sealed class FakeModbusClient : IModbusClient
    {
        private static readonly ushort[] Packet =
        [
            0xCB01, 0x0002, 0x0000, 0x0011,
            0x6A8E, 0xC154, 0x0003, 0x0002,
            0x0001, 0x0001, 0xE208, 0x41AE
        ];

        public List<ModbusWrite> Writes { get; } = [];

        public Task<ushort[]> ReadHoldingRegistersAsync(
            ushort address,
            ushort count,
            CancellationToken cancellationToken)
        {
            if (count == EventProtocol.SnapshotWords)
                return Task.FromResult(Packet.Concat(new ushort[] { 1, 1, 128, 1 }).ToArray());
            if (count == 21)
            {
                var verification = new ushort[21];
                verification[EventProtocol.EventReadyIndex] = 0;
                verification[EventProtocol.AckCommandAddress - EventProtocol.EventBaseAddress] = 0;
                verification[EventProtocol.LastAckResultAddress - EventProtocol.EventBaseAddress] = 1;
                return Task.FromResult(verification);
            }
            throw new InvalidOperationException($"Unexpected read count {count}.");
        }

        public Task WriteSingleRegisterAsync(
            ushort address,
            ushort value,
            CancellationToken cancellationToken)
        {
            Writes.Add(new ModbusWrite("single", address, [value]));
            return Task.CompletedTask;
        }

        public Task WriteMultipleRegistersAsync(
            ushort address,
            IReadOnlyList<ushort> values,
            CancellationToken cancellationToken)
        {
            Writes.Add(new ModbusWrite("multiple", address, values.ToArray()));
            return Task.CompletedTask;
        }
    }
}
