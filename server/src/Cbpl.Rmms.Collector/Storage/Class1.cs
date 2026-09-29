using System.Data;
using System.Globalization;
using Cbpl.Rmms.Collector.Configuration;
using Cbpl.Rmms.Collector.Protocol;
using Microsoft.Data.SqlClient;

namespace Cbpl.Rmms.Collector.Storage;

public sealed class SqlServerEventSync(
    SqlServerOptions options,
    EventStore store)
{
    public static async Task CheckConnectionAsync(
        SqlServerOptions options,
        CancellationToken cancellationToken)
    {
        await using SqlConnection connection =
            await OpenCheckedAsync(options, cancellationToken);
    }

    public async Task<int> SyncOnceAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<PendingCentralEvent> pending =
            await store.GetPendingCentralEventsAsync(
                options.BatchSize, cancellationToken);

        if (pending.Count == 0)
            return 0;

        await using SqlConnection connection =
            await OpenCheckedAsync(options, cancellationToken);

        int synced = 0;
        string schema = GetSqlSchema(options.Schema);
        foreach (PendingCentralEvent item in pending)
        {
            // SQLite помечаем только ПОСЛЕ успешного ответа SQL Server.
            await WriteEventAsync(
                connection, schema, options.ControllerCode, item, cancellationToken);
            await store.MarkCentralSyncedAsync(
                item.LocalId, cancellationToken);
            synced++;
        }

        return synced;
    }

    internal static async Task<SqlConnection> OpenCheckedAsync(
        SqlServerOptions options,
        CancellationToken cancellationToken)
    {
        string schema = GetSqlSchema(options.Schema);

        var connection = new SqlConnection(options.ConnectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);

            if (!string.Equals(
                    connection.Database, "RMMS_Demo",
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Connected to unexpected database: {connection.Database}");

            await using SqlCommand check = connection.CreateCommand();
            check.CommandText = $"""
                SELECT COUNT(*)
                FROM {schema}.[Controllers]
                WHERE ControllerCode = @code AND IsEnabled = 1;
                """;
            check.Parameters.AddWithValue("@code", options.ControllerCode);

            if (await check.ExecuteScalarAsync(cancellationToken) is not int count
                || count != 1)
                throw new InvalidOperationException(
                    $"Controller {options.ControllerCode} not found or disabled.");

            await using SqlCommand schemaCheck = connection.CreateCommand();
            schemaCheck.CommandText = $"""
                SELECT TOP (0)
                    RollNumber, IsConfigured, IsActive, CurrentLengthMm,
                    PreviousLengthMm, SpeedMmPerMinute, SpliceSignal,
                    ResetSignal, InputError, UpdatedAtUtc
                FROM {schema}.[RollLiveState];

                SELECT TOP (0)
                    CollectorName, ControllerId, PlcOnline, PlcQueueCount,
                    PlcOverflowCount, PlcHeartbeat, LastPollAtUtc,
                    LastEventAtUtc, LastError, UpdatedAtUtc
                FROM {schema}.[CollectorStatus];
                """;
            await schemaCheck.ExecuteNonQueryAsync(cancellationToken);

            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static async Task WriteEventAsync(
        SqlConnection connection,
        string schema,
        string controllerCode,
        PendingCentralEvent item,
        CancellationToken cancellationToken)
    {
        string[] parts = item.RawPacketHex.Split(
            ' ', StringSplitOptions.RemoveEmptyEntries);
        ushort[] words = Array.ConvertAll(
            parts,
            part => ushort.Parse(
                part, NumberStyles.HexNumber, CultureInfo.InvariantCulture));

        if (words.Length != EventProtocol.EventPacketWords
            || words[0] != EventProtocol.Signature
            || words[1] != EventProtocol.Version
            || words[11] != EventProtocol.CalculateCrc(words.AsSpan(0, 11)))
            throw new InvalidDataException(
                $"Invalid stored packet for EventId {item.PlcEventId}.");

        DateTime? eventTimeUtc = item.EventTimeValid
            ? DateTimeOffset.FromUnixTimeSeconds(item.PlcUnixTime).UtcDateTime
            : null;

        DateTime receivedAtUtc = DateTimeOffset.Parse(
            item.ReceivedAtUtc, CultureInfo.InvariantCulture).UtcDateTime;

        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = $"""
            SET XACT_ABORT ON;

            BEGIN TRY
                BEGIN TRANSACTION;

                DECLARE @ControllerId int;

                SELECT @ControllerId = ControllerId
                FROM {schema}.[Controllers]
                WHERE ControllerCode = @ControllerCode AND IsEnabled = 1;

                IF @ControllerId IS NULL
                    THROW 51000, 'Controller not found or disabled.', 1;

                IF NOT EXISTS (
                    SELECT 1
                    FROM {schema}.[RollEvents] WITH (UPDLOCK, HOLDLOCK)
                    WHERE ControllerId = @ControllerId
                      AND PlcEventId = @PlcEventId
                )
                BEGIN
                    INSERT INTO {schema}.[RollEvents]
                    (
                        ControllerId, PlcEventId, PlcUnixTime,
                        EventTimeUtc, EventTimeValid, RollNumber,
                        ShiftNumber, EventType, LengthMm,
                        ProtocolVersion, Crc16, ReceivedAtUtc
                    )
                    VALUES
                    (
                        @ControllerId, @PlcEventId, @PlcUnixTime,
                        @EventTimeUtc, @EventTimeValid, @RollNumber,
                        @ShiftNumber, @EventType, @LengthMm,
                        @ProtocolVersion, @Crc16, @ReceivedAtUtc
                    );
                END;

                -- Если ключ уже был, это должно быть ТО ЖЕ событие.
                IF NOT EXISTS (
                    SELECT 1
                    FROM {schema}.[RollEvents]
                    WHERE ControllerId = @ControllerId
                      AND PlcEventId = @PlcEventId
                      AND PlcUnixTime = @PlcUnixTime
                      AND EventTimeValid = @EventTimeValid
                      AND (
                          EventTimeUtc = @EventTimeUtc
                          OR (EventTimeUtc IS NULL AND @EventTimeUtc IS NULL)
                      )
                      AND RollNumber = @RollNumber
                      AND ShiftNumber = @ShiftNumber
                      AND EventType = @EventType
                      AND LengthMm = @LengthMm
                      AND ProtocolVersion = @ProtocolVersion
                      AND Crc16 = @Crc16
                )
                    THROW 51001, 'Conflicting data for existing PlcEventId.', 1;

                COMMIT TRANSACTION;
            END TRY
            BEGIN CATCH
                IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
                THROW;
            END CATCH;
            """;

        command.Parameters.AddWithValue("@ControllerCode", controllerCode);
        command.Parameters.AddWithValue("@PlcEventId", item.PlcEventId);
        command.Parameters.AddWithValue("@PlcUnixTime", item.PlcUnixTime);
        command.Parameters.Add("@EventTimeUtc", SqlDbType.DateTime2).Value =
            eventTimeUtc.HasValue ? eventTimeUtc.Value : DBNull.Value;
        command.Parameters.AddWithValue(
            "@EventTimeValid", item.EventTimeValid);
        command.Parameters.AddWithValue(
            "@RollNumber", checked((byte)item.RollNumber));
        command.Parameters.AddWithValue(
            "@ShiftNumber", checked((byte)item.ShiftNumber));
        command.Parameters.AddWithValue(
            "@EventType", checked((byte)item.EventType));
        command.Parameters.AddWithValue("@LengthMm", item.LengthMm);
        command.Parameters.AddWithValue(
            "@ProtocolVersion", checked((short)words[1]));
        command.Parameters.AddWithValue("@Crc16", (int)words[11]);
        command.Parameters.Add("@ReceivedAtUtc", SqlDbType.DateTime2).Value =
            receivedAtUtc;

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    internal static string GetSqlSchema(string schema) => schema switch
    {
        "rmms_test" => "[rmms_test]",
        "rmms" => "[rmms]",
        _ => throw new InvalidOperationException(
            "Only the rmms_test and rmms SQL schemas are allowed.")
    };
}
