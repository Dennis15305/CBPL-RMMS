using Cbpl.Rmms.Collector.Protocol;
using Microsoft.Data.Sqlite;

namespace Cbpl.Rmms.Collector.Storage;

public sealed class EventStore(string databasePath, string schemaPath)
{
    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Shared,
        Pooling = false
    }.ToString();

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        string schema = await File.ReadAllTextAsync(schemaPath, cancellationToken);
        await using SqliteConnection connection = await OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = schema;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> SaveEventAsync(RollEvent rollEvent, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await OpenAsync(cancellationToken);
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO roll_events (
                plc_event_id, plc_timestamp_utc, plc_timestamp_valid,
                unwind_number, shift_number, end_reason, length_mm,
                received_at_utc, raw_packet_hex
            ) VALUES (
                $eventId, $timestamp, $timestampValid,
                $unwind, $shift, $reason, $length,
                $receivedAt, $rawPacket
            )
            ON CONFLICT(plc_event_id) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$eventId", rollEvent.EventId);
        command.Parameters.AddWithValue("$timestamp", rollEvent.TimestampUtc);
        command.Parameters.AddWithValue("$timestampValid", rollEvent.TimestampValid ? 1 : 0);
        command.Parameters.AddWithValue("$unwind", rollEvent.UnwindNumber);
        command.Parameters.AddWithValue("$shift", rollEvent.ShiftNumber);
        command.Parameters.AddWithValue("$reason", (ushort)rollEvent.EndReason);
        command.Parameters.AddWithValue("$length", rollEvent.LengthMm);
        command.Parameters.AddWithValue("$receivedAt", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$rawPacket", rollEvent.RawPacketHex);
        int affected = await command.ExecuteNonQueryAsync(cancellationToken);
        if (affected == 0)
        {
            await using SqliteCommand existing = connection.CreateCommand();
            existing.Transaction = transaction;
            existing.CommandText =
                "SELECT raw_packet_hex FROM roll_events WHERE plc_event_id = $eventId;";
            existing.Parameters.AddWithValue("$eventId", rollEvent.EventId);

            string? storedPacket =
                await existing.ExecuteScalarAsync(cancellationToken) as string;

            if (!string.Equals(
                    storedPacket, rollEvent.RawPacketHex, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"EventId {rollEvent.EventId} already exists with different data.");
        }
        await transaction.CommitAsync(cancellationToken);
        return affected == 1;
    }

    public async Task<bool> HasEventAsync(uint eventId, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM roll_events WHERE plc_event_id = $eventId LIMIT 1;";
        command.Parameters.AddWithValue("$eventId", eventId);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    public async Task MarkPlcAcknowledgedAsync(uint eventId, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE roll_events
            SET plc_acknowledged_at_utc = COALESCE(plc_acknowledged_at_utc, $acknowledgedAt)
            WHERE plc_event_id = $eventId;
            """;
        command.Parameters.AddWithValue("$acknowledgedAt", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$eventId", eventId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException(
                $"Cannot mark unknown EventId {eventId} as acknowledged.");
    }
    public async Task<IReadOnlyList<PendingCentralEvent>>
    GetPendingCentralEventsAsync(int limit, CancellationToken cancellationToken)
    {
        if (limit is < 1 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(limit));

        await using SqliteConnection connection = await OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = """
        SELECT id, plc_event_id, plc_timestamp_utc, plc_timestamp_valid,
               unwind_number, shift_number, end_reason, length_mm,
               received_at_utc, raw_packet_hex
        FROM roll_events
        WHERE central_sync_state <> 'synced'
        ORDER BY id
        LIMIT $limit;
        """;
        command.Parameters.AddWithValue("$limit", limit);

        var events = new List<PendingCentralEvent>();
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            events.Add(new PendingCentralEvent(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetInt64(2),
                reader.GetInt64(3) == 1,
                checked((int)reader.GetInt64(4)),
                checked((int)reader.GetInt64(5)),
                checked((int)reader.GetInt64(6)),
                reader.GetInt64(7),
                reader.GetString(8),
                reader.GetString(9)));
        }

        return events;
    }
    public async Task MarkCentralSyncedAsync(
    long localId,
    CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = """
        UPDATE roll_events
        SET central_sync_state = 'synced',
            central_attempts = central_attempts + 1,
            central_last_attempt_at_utc = $now,
            central_synced_at_utc = COALESCE(central_synced_at_utc, $now),
            central_last_error = NULL
        WHERE id = $localId;
        """;

        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$localId", localId);

        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException(
                $"Cannot mark unknown local event {localId} as synced.");
    }

    public async Task SetMetaAsync(
        string key,
        string value,
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO collector_meta(key, value, updated_at_utc)
            VALUES ($key, $value, $updatedAt)
            ON CONFLICT(key) DO UPDATE SET
                value = excluded.value,
                updated_at_utc = excluded.updated_at_utc;
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            await using SqliteCommand pragma = connection.CreateCommand();
            pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA synchronous = FULL;";
            await pragma.ExecuteNonQueryAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}
