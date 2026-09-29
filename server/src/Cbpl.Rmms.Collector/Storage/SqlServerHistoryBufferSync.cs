using System.Data;
using Cbpl.Rmms.Collector.Configuration;
using Cbpl.Rmms.Collector.Modbus;
using Cbpl.Rmms.Collector.Protocol;
using Microsoft.Data.SqlClient;

namespace Cbpl.Rmms.Collector.Storage;

public sealed class SqlServerHistoryBufferSync(
    SqlServerOptions sqlOptions,
    IModbusClient modbus)
{
    public async Task SyncOnceAsync(CancellationToken cancellationToken)
    {
        ushort[] requestWords = await modbus.ReadHoldingRegistersAsync(
            HistoryBufferProtocol.RequestBaseAddress,
            HistoryBufferProtocol.RequestWordCount,
            cancellationToken);
        var (rollNumber, requestedPage, _) =
            HistoryBufferProtocol.ParseRequest(requestWords);

        if (rollNumber == 0)
        {
            await WriteResponseAsync(
                status: 0, rollNumber: 0, pageNumber: 1,
                pageCount: 0, totalRows: 0, [], cancellationToken);
            return;
        }

        if (rollNumber is < 1 or > 5)
        {
            await WriteResponseAsync(
                status: 2, rollNumber, pageNumber: 1,
                pageCount: 0, totalRows: 0, [], cancellationToken);
            return;
        }

        await using SqlConnection connection =
            await SqlServerEventSync.OpenCheckedAsync(sqlOptions, cancellationToken);
        string schema = SqlServerEventSync.GetSqlSchema(sqlOptions.Schema);

        int totalRows = await ReadCountAsync(
            connection, schema, rollNumber, cancellationToken);
        int pageCount = Math.Max(
            1, (totalRows + HistoryBufferProtocol.RowsPerPage - 1)
                / HistoryBufferProtocol.RowsPerPage);
        int pageNumber = Math.Clamp((int)requestedPage, 1, pageCount);
        IReadOnlyList<HistoryBufferRow> rows = await ReadPageAsync(
            connection, schema, rollNumber, pageNumber, cancellationToken);

        await WriteResponseAsync(
            status: 1,
            rollNumber,
            checked((ushort)pageNumber),
            checked((ushort)pageCount),
            checked((ushort)Math.Min(totalRows, ushort.MaxValue)),
            rows,
            cancellationToken);
    }

    private static async Task<int> ReadCountAsync(
        SqlConnection connection,
        string schema,
        ushort rollNumber,
        CancellationToken cancellationToken)
    {
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT COUNT_BIG(*)
            FROM {schema}.[vRollEventsCurrentShift]
            WHERE [RollNumber] = @RollNumber;
            """;
        command.Parameters.Add("@RollNumber", SqlDbType.TinyInt).Value =
            checked((byte)rollNumber);
        long count = (long)(await command.ExecuteScalarAsync(cancellationToken) ?? 0L);
        return checked((int)Math.Min(count, int.MaxValue));
    }

    private static async Task<IReadOnlyList<HistoryBufferRow>> ReadPageAsync(
        SqlConnection connection,
        string schema,
        ushort rollNumber,
        int pageNumber,
        CancellationToken cancellationToken)
    {
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT
                [Id],
                CONVERT(bigint, ROUND([Length] * 10.0, 0)) AS [LengthX10],
                [EventType],
                [ShiftNumber],
                DATEPART(HOUR, [EventTime]) AS [LocalHour],
                DATEPART(MINUTE, [EventTime]) AS [LocalMinute]
            FROM {schema}.[vRollEventsCurrentShift]
            WHERE [RollNumber] = @RollNumber
            ORDER BY [Id] DESC
            OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY;
            """;
        command.Parameters.Add("@RollNumber", SqlDbType.TinyInt).Value =
            checked((byte)rollNumber);
        command.Parameters.Add("@Offset", SqlDbType.Int).Value =
            (pageNumber - 1) * HistoryBufferProtocol.RowsPerPage;
        command.Parameters.Add("@PageSize", SqlDbType.Int).Value =
            HistoryBufferProtocol.RowsPerPage;

        var rows = new List<HistoryBufferRow>(HistoryBufferProtocol.RowsPerPage);
        await using SqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new HistoryBufferRow(
                checked((uint)reader.GetInt64(0)),
                checked((uint)reader.GetInt64(1)),
                checked((ushort)reader.GetByte(2)),
                checked((ushort)reader.GetByte(3)),
                checked((ushort)reader.GetInt32(4)),
                checked((ushort)reader.GetInt32(5))));
        }

        return rows;
    }

    private Task WriteResponseAsync(
        ushort status,
        ushort rollNumber,
        ushort pageNumber,
        ushort pageCount,
        ushort totalRows,
        IReadOnlyList<HistoryBufferRow> rows,
        CancellationToken cancellationToken)
    {
        ushort[] words = HistoryBufferProtocol.EncodeResponse(
            status, rollNumber, pageNumber, pageCount, totalRows, rows);
        return modbus.WriteMultipleRegistersAsync(
            HistoryBufferProtocol.ResponseBaseAddress, words, cancellationToken);
    }
}
