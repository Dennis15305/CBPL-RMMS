using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using Cbpl.Rmms.Collector.Configuration;
using Cbpl.Rmms.Collector.Protocol;
using Microsoft.Data.SqlClient;

namespace Cbpl.Rmms.Collector.Storage;

public sealed class SqlServerStopJournalSink(
    SqlServerOptions sqlOptions,
    StopJournalOptions journalOptions) : IStopJournalSink
{
    public async Task CheckTargetAsync(CancellationToken cancellationToken)
    {
        await using SqlConnection connection = await OpenAsync(cancellationToken);
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT TOP (0)
                [LineName], [OrderLength], [OrderArea], [LineSpeed],
                [ShiftNumber], [StateCode], [EventTime], [OrderNumber], [OperatorNumber],
                [ChangeoverMinutes], [AlarmBits]
            FROM [dbo].[RMMS_EventSink];

            SELECT TOP (0)
                [ControllerCode], [SourceEpoch], [PlcEventId],
                [PlcUnixTime], [PayloadFingerprint], [InsertedAtUtc]
            FROM [dbo].[RMMS_EventReceipt];
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> StoreAsync(
        StopJournalEvent item,
        CancellationToken cancellationToken)
    {
        byte[] fingerprint = Fingerprint(item.RawWords);
        DateTime localEventTime = ResolveMoscowTime(item.TimestampUtc);

        await using SqlConnection connection = await OpenAsync(cancellationToken);
        await using SqlTransaction transaction =
            (SqlTransaction)await connection.BeginTransactionAsync(
                IsolationLevel.Serializable, cancellationToken);
        try
        {
            await using SqlCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                DECLARE @ExistingFingerprint varbinary(32);

                SELECT @ExistingFingerprint = [PayloadFingerprint]
                FROM [dbo].[RMMS_EventReceipt] WITH (UPDLOCK, HOLDLOCK)
                WHERE [ControllerCode] = @ControllerCode
                  AND [SourceEpoch] = @SourceEpoch
                  AND [PlcEventId] = @PlcEventId;

                IF @ExistingFingerprint IS NULL
                BEGIN
                    INSERT INTO [dbo].[RMMS_EventSink]
                    (
                        [EventTime], [LineName], [OrderNumber], [ShiftNumber], [StateCode],
                        [OrderLength], [OrderArea], [LineSpeed],
                        [OperatorNumber], [ChangeoverMinutes], [AlarmBits]
                    )
                    VALUES
                    (
                        @EventTime, @LineName, @OrderNumber, @ShiftNumber, @StateCode,
                        @InputCounter, @OutputCounter, @Speed, @OperatorNumber,
                        @Changeover, @Status2
                    );

                    INSERT INTO [dbo].[RMMS_EventReceipt]
                    (
                        [ControllerCode], [SourceEpoch], [PlcEventId],
                        [PlcUnixTime], [PayloadFingerprint], [InsertedAtUtc]
                    )
                    VALUES
                    (
                        @ControllerCode, @SourceEpoch, @PlcEventId,
                        @PlcUnixTime, @PayloadFingerprint, SYSUTCDATETIME()
                    );

                    SELECT CAST(1 AS int);
                END
                ELSE
                BEGIN
                    IF @ExistingFingerprint <> @PayloadFingerprint
                        THROW 51020, 'Conflicting payload for existing stop journal event ID.', 1;

                    SELECT CAST(0 AS int);
                END;
                """;

            command.Parameters.AddWithValue("@ControllerCode", sqlOptions.ControllerCode);
            command.Parameters.AddWithValue("@SourceEpoch", journalOptions.SourceEpoch);
            command.Parameters.AddWithValue("@PlcEventId", (long)item.EventId);
            command.Parameters.AddWithValue("@PlcUnixTime", (long)item.TimestampUtc);
            command.Parameters.Add("@PayloadFingerprint", SqlDbType.VarBinary, 32).Value = fingerprint;
            command.Parameters.Add("@EventTime", SqlDbType.DateTime).Value = localEventTime;
            command.Parameters.AddWithValue("@LineName", journalOptions.LineName);
            command.Parameters.AddWithValue("@OrderNumber", Text(item.OrderNumber));
            command.Parameters.AddWithValue("@ShiftNumber", Text(item.ShiftNumber));
            command.Parameters.AddWithValue("@StateCode", Text(item.StateCode));
            command.Parameters.AddWithValue(
                "@InputCounter", Scaled(item.OrderLengthDm, 10, 1, 10, "length"));
            command.Parameters.AddWithValue(
                "@OutputCounter", Scaled(item.OrderAreaX100, 100, 2, 10, "area"));
            command.Parameters.AddWithValue(
                "@Speed", Scaled(item.SpeedDmPerMinute, 10, 1, 10, "speed"));
            command.Parameters.AddWithValue("@OperatorNumber", Text(item.OperatorNumber));
            command.Parameters.AddWithValue(
                "@Changeover",
                Scaled(item.ChangeoverSetpointTenthsMin, 10, 1, 15, "changeover"));
            command.Parameters.AddWithValue("@Status2", Text(item.AlarmBits));

            object? scalar = await command.ExecuteScalarAsync(cancellationToken);
            bool inserted = Convert.ToInt32(scalar, CultureInfo.InvariantCulture) == 1;
            await transaction.CommitAsync(cancellationToken);
            return inserted;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(sqlOptions.ConnectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            if (!string.Equals(connection.Database, "RMMS_Demo", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Connected to unexpected database: {connection.Database}");
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static string Text<T>(T value) where T : IFormattable =>
        value.ToString(null, CultureInfo.InvariantCulture);

    private static string Scaled(
        uint value,
        uint divisor,
        int decimals,
        int maximumLength,
        string field)
    {
        string result = ((decimal)value / divisor).ToString(
            $"F{decimals}", CultureInfo.InvariantCulture);
        if (result.Length > maximumLength)
            throw new InvalidDataException(
                $"Stop journal {field} value '{result}' does not fit the legacy column.");
        return result;
    }

    private static DateTime ResolveMoscowTime(uint timestampUtc)
    {
        DateTime utc = timestampUtc == 0
            ? DateTime.UtcNow
            : DateTimeOffset.FromUnixTimeSeconds(timestampUtc).UtcDateTime;
        // Москва с 2014 года постоянно UTC+3. Kind=Unspecified нужен для SQL datetime.
        return DateTime.SpecifyKind(utc.AddHours(3), DateTimeKind.Unspecified);
    }

    private static byte[] Fingerprint(IReadOnlyList<ushort> words)
    {
        byte[] bytes = new byte[words.Count * 2];
        for (int index = 0; index < words.Count; index++)
        {
            bytes[index * 2] = (byte)(words[index] >> 8);
            bytes[index * 2 + 1] = (byte)words[index];
        }
        return SHA256.HashData(bytes);
    }
}
