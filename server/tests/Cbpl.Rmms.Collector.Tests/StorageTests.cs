using Cbpl.Rmms.Collector.Protocol;
using Cbpl.Rmms.Collector.Storage;
using Xunit;

namespace Cbpl.Rmms.Collector.Tests;

public sealed class StorageTests
{
    [Fact]
    public async Task EventIdIsStoredIdempotently()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"cbpl-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string schema = Path.Combine(AppContext.BaseDirectory, "schema.sql");
            var store = new EventStore(Path.Combine(directory, "events.db"), schema);
            await store.InitializeAsync(CancellationToken.None);
            var rollEvent = new RollEvent(
                17,
                1_787_740_500,
                3,
                2,
                RollEndReason.Splice,
                123_400,
                [0xCB01, 2, 0, 17, 0x6A8E, 0xC154, 3, 2, 1, 1, 0xE208, 0x41AE]);

            Assert.True(await store.SaveEventAsync(rollEvent, CancellationToken.None));
            Assert.False(await store.SaveEventAsync(rollEvent, CancellationToken.None));
            Assert.True(await store.HasEventAsync(17, CancellationToken.None));
            PendingCentralEvent item = Assert.Single(
                await store.GetPendingCentralEventsAsync(20, CancellationToken.None));

            Assert.Equal(17L, item.PlcEventId);
            Assert.Equal(123_400L, item.LengthMm);

            await store.MarkCentralSyncedAsync(item.LocalId, CancellationToken.None);

            Assert.Empty(
                await store.GetPendingCentralEventsAsync(20, CancellationToken.None));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
