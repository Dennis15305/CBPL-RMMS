using System.Data;
using Cbpl.Rmms.Collector.Configuration;
using Cbpl.Rmms.Collector.Modbus;
using Cbpl.Rmms.Collector.Protocol;
using Microsoft.Data.SqlClient;

namespace Cbpl.Rmms.Collector.Storage;

public sealed class SqlServerOperationalStateSync(
    SqlServerOptions options,
    IModbusClient modbus)
{
    public async Task<string?> SyncOnceAsync(CancellationToken cancellationToken)
    {
        PlcOperationalSnapshot snapshot;
        try
        {
            ushort[] words = await modbus.ReadHoldingRegistersAsync(
                OperationalStateProtocol.BaseAddress,
                OperationalStateProtocol.WordCount,
                cancellationToken);
            snapshot = OperationalStateProtocol.Parse(words);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            string plcError = Limit(exception.Message, 1000);
            await WriteOfflineAsync(plcError, cancellationToken);
            return plcError;
        }

        await WriteOnlineAsync(snapshot, cancellationToken);
        return null;
    }

    private async Task WriteOnlineAsync(
        PlcOperationalSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        await using SqlConnection connection =
            await SqlServerEventSync.OpenCheckedAsync(options, cancellationToken);
        string schema = SqlServerEventSync.GetSqlSchema(options.Schema);

        await using SqlTransaction transaction =
            (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            int controllerId = await GetControllerIdAsync(
                connection, transaction, schema, cancellationToken);
            DateTime updatedAtUtc = DateTime.UtcNow;

            foreach (RollLiveSnapshot roll in snapshot.Rolls)
            {
                await UpsertRollAsync(
                    connection, transaction, schema, controllerId,
                    roll, updatedAtUtc, cancellationToken);
            }

            await UpsertCollectorStatusAsync(
                connection, transaction, schema, controllerId,
                true, snapshot.QueueCount, snapshot.QueueOverflowCount,
                snapshot.PlcHeartbeat, updatedAtUtc, null, updatedAtUtc,
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private async Task WriteOfflineAsync(
        string error,
        CancellationToken cancellationToken)
    {
        await using SqlConnection connection =
            await SqlServerEventSync.OpenCheckedAsync(options, cancellationToken);
        string schema = SqlServerEventSync.GetSqlSchema(options.Schema);

        await using SqlTransaction transaction =
            (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            int controllerId = await GetControllerIdAsync(
                connection, transaction, schema, cancellationToken);

            await UpsertCollectorStatusAsync(
                connection, transaction, schema, controllerId,
                false, 0, 0, null, null, error, DateTime.UtcNow,
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private async Task<int> GetControllerIdAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string schema,
        CancellationToken cancellationToken)
    {
        await using SqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT ControllerId
            FROM {schema}.[Controllers]
            WHERE ControllerCode = @ControllerCode AND IsEnabled = 1;
            """;
        command.Parameters.AddWithValue("@ControllerCode", options.ControllerCode);

        object? result = await command.ExecuteScalarAsync(cancellationToken);
        if (result is not int controllerId)
            throw new InvalidOperationException(
                $"Controller {options.ControllerCode} not found or disabled.");
        return controllerId;
    }

    private static async Task UpsertRollAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string schema,
        int controllerId,
        RollLiveSnapshot roll,
        DateTime updatedAtUtc,
        CancellationToken cancellationToken)
    {
        await using SqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            UPDATE {schema}.[RollLiveState]
            SET IsConfigured = @IsConfigured,
                IsActive = @IsActive,
                CurrentLengthMm = @CurrentLengthMm,
                PreviousLengthMm = @PreviousLengthMm,
                SpeedMmPerMinute = @SpeedMmPerMinute,
                SpliceSignal = @SpliceSignal,
                ResetSignal = @ResetSignal,
                InputError = @InputError,
                UpdatedAtUtc = @UpdatedAtUtc
            WHERE ControllerId = @ControllerId AND RollNumber = @RollNumber;

            IF @@ROWCOUNT = 0
            BEGIN
                INSERT INTO {schema}.[RollLiveState]
                (
                    ControllerId, RollNumber, IsConfigured, IsActive,
                    CurrentLengthMm, PreviousLengthMm, SpeedMmPerMinute,
                    SpliceSignal, ResetSignal, InputError, UpdatedAtUtc
                )
                VALUES
                (
                    @ControllerId, @RollNumber, @IsConfigured, @IsActive,
                    @CurrentLengthMm, @PreviousLengthMm, @SpeedMmPerMinute,
                    @SpliceSignal, @ResetSignal, @InputError, @UpdatedAtUtc
                );
            END;
            """;
        command.Parameters.AddWithValue("@ControllerId", controllerId);
        command.Parameters.AddWithValue("@RollNumber", roll.RollNumber);
        command.Parameters.AddWithValue("@IsConfigured", roll.IsConfigured);
        command.Parameters.AddWithValue("@IsActive", roll.IsActive);
        command.Parameters.AddWithValue("@CurrentLengthMm", roll.CurrentLengthMm);
        command.Parameters.AddWithValue("@PreviousLengthMm", roll.PreviousLengthMm);
        command.Parameters.AddWithValue("@SpeedMmPerMinute", roll.SpeedMmPerMinute);
        command.Parameters.AddWithValue("@SpliceSignal", roll.SpliceSignal);
        command.Parameters.AddWithValue("@ResetSignal", roll.ResetSignal);
        command.Parameters.AddWithValue("@InputError", roll.InputError);
        command.Parameters.Add("@UpdatedAtUtc", SqlDbType.DateTime2).Value = updatedAtUtc;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task UpsertCollectorStatusAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string schema,
        int controllerId,
        bool plcOnline,
        int queueCount,
        long queueOverflowCount,
        long? plcHeartbeat,
        DateTime? lastPollAtUtc,
        string? lastError,
        DateTime updatedAtUtc,
        CancellationToken cancellationToken)
    {
        await using SqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            DECLARE @LastEventAtUtc datetime2(3) =
            (
                SELECT MAX(ReceivedAtUtc)
                FROM {schema}.[RollEvents]
                WHERE ControllerId = @ControllerId
            );

            UPDATE {schema}.[CollectorStatus]
            SET ControllerId = @ControllerId,
                PlcOnline = @PlcOnline,
                PlcQueueCount = CASE WHEN @PlcOnline = 1 THEN @PlcQueueCount ELSE PlcQueueCount END,
                PlcOverflowCount = CASE WHEN @PlcOnline = 1 THEN @PlcOverflowCount ELSE PlcOverflowCount END,
                PlcHeartbeat = CASE WHEN @PlcOnline = 1 THEN @PlcHeartbeat ELSE PlcHeartbeat END,
                LastPollAtUtc = CASE WHEN @PlcOnline = 1 THEN @LastPollAtUtc ELSE LastPollAtUtc END,
                LastEventAtUtc = COALESCE(@LastEventAtUtc, LastEventAtUtc),
                LastError = @LastError,
                UpdatedAtUtc = @UpdatedAtUtc
            WHERE CollectorName = @CollectorName;

            IF @@ROWCOUNT = 0
            BEGIN
                INSERT INTO {schema}.[CollectorStatus]
                (
                    CollectorName, ControllerId, PlcOnline, PlcQueueCount,
                    PlcOverflowCount, PlcHeartbeat, LastPollAtUtc,
                    LastEventAtUtc, LastError, UpdatedAtUtc
                )
                VALUES
                (
                    @CollectorName, @ControllerId, @PlcOnline, @PlcQueueCount,
                    @PlcOverflowCount, @PlcHeartbeat, @LastPollAtUtc,
                    @LastEventAtUtc, @LastError, @UpdatedAtUtc
                );
            END;
            """;
        command.Parameters.AddWithValue("@CollectorName", options.ControllerCode);
        command.Parameters.AddWithValue("@ControllerId", controllerId);
        command.Parameters.AddWithValue("@PlcOnline", plcOnline);
        command.Parameters.AddWithValue("@PlcQueueCount", queueCount);
        command.Parameters.AddWithValue("@PlcOverflowCount", queueOverflowCount);
        command.Parameters.Add("@PlcHeartbeat", SqlDbType.BigInt).Value =
            plcHeartbeat.HasValue ? plcHeartbeat.Value : DBNull.Value;
        command.Parameters.Add("@LastPollAtUtc", SqlDbType.DateTime2).Value =
            lastPollAtUtc.HasValue ? lastPollAtUtc.Value : DBNull.Value;
        command.Parameters.Add("@LastError", SqlDbType.NVarChar, 1000).Value =
            lastError is null ? DBNull.Value : lastError;
        command.Parameters.Add("@UpdatedAtUtc", SqlDbType.DateTime2).Value = updatedAtUtc;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string Limit(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
