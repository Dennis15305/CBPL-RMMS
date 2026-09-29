using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;

namespace Cbpl.Rmms.EventJournal;

public sealed record JournalEvent(
    string Id,
    int RollNumber,
    decimal Length,
    int EventType,
    int ShiftNumber,
    string EventTime);

public sealed record JournalPage(
    IReadOnlyList<JournalEvent> Items,
    bool HasMore,
    string? NextBeforeId);

public sealed class EventRepository(JournalOptions options)
{
    public const int PageSize = 7;
    public const int RollPageSize = 6;

    public Task<JournalPage> ReadPageAsync(
        long? beforeId,
        CancellationToken cancellationToken) =>
        ReadPageCoreAsync(beforeId, null, PageSize, cancellationToken);

    public Task<JournalPage> ReadRollPageAsync(
        int rollNumber,
        long? beforeId,
        CancellationToken cancellationToken)
    {
        if (rollNumber is < 1 or > 5)
            throw new ArgumentOutOfRangeException(nameof(rollNumber));

        return ReadPageCoreAsync(
            beforeId, rollNumber, RollPageSize, cancellationToken);
    }

    private async Task<JournalPage> ReadPageCoreAsync(
        long? beforeId,
        int? rollNumber,
        int pageSize,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(options.SqlConnectionString);
        await connection.OpenAsync(cancellationToken);

        if (!string.Equals(connection.Database, "RMMS_Demo",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Connected to an unexpected database.");
        }

        await using SqlCommand command = connection.CreateCommand();
        command.CommandTimeout = 10;
        command.CommandText = rollNumber.HasValue
            ? """
                SELECT TOP (@Take)
                    [Id], [RollNumber], [Length], [EventType],
                    [ShiftNumber], [EventTime]
                FROM [rmms].[vRollEventsCurrentShift]
                WHERE [RollNumber] = @RollNumber
                  AND (@BeforeId IS NULL OR [Id] < @BeforeId)
                ORDER BY [Id] DESC;
                """
            : """
                SELECT TOP (@Take)
                    [Id], [RollNumber], [Length], [EventType],
                    [ShiftNumber], [EventTime]
                FROM [rmms].[vHaiwellRollEvents]
                WHERE @BeforeId IS NULL OR [Id] < @BeforeId
                ORDER BY [Id] DESC;
                """;
        command.Parameters.Add("@Take", SqlDbType.Int).Value = pageSize + 1;
        command.Parameters.Add("@BeforeId", SqlDbType.BigInt).Value =
            beforeId.HasValue ? (object)beforeId.Value : DBNull.Value;
        if (rollNumber.HasValue)
            command.Parameters.Add("@RollNumber", SqlDbType.Int).Value = rollNumber.Value;

        var items = new List<JournalEvent>(pageSize + 1);
        await using SqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new JournalEvent(
                reader.GetInt64(0).ToString(CultureInfo.InvariantCulture),
                Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture),
                Convert.ToDecimal(reader.GetValue(2), CultureInfo.InvariantCulture),
                Convert.ToInt32(reader.GetValue(3), CultureInfo.InvariantCulture),
                Convert.ToInt32(reader.GetValue(4), CultureInfo.InvariantCulture),
                reader.GetDateTime(5).ToString(
                    "dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture)));
        }

        bool hasMore = items.Count > pageSize;
        if (hasMore)
            items.RemoveAt(pageSize);

        return new JournalPage(
            items,
            hasMore,
            items.Count > 0 ? items[^1].Id : null);
    }
}
