namespace Cbpl.Rmms.Collector.Protocol;

public sealed record HistoryBufferRow(
    uint Id,
    uint LengthX10,
    ushort EventType,
    ushort ShiftNumber,
    ushort LocalHour,
    ushort LocalMinute);

public static class HistoryBufferProtocol
{
    public const ushort RequestBaseAddress = 2157;
    public const ushort RequestWordCount = 3;
    public const ushort ResponseBaseAddress = 2160;
    public const ushort ResponseWordCount = 40;
    public const ushort Signature = 0x4853; // "HS"
    public const ushort Version = 1;
    public const int RowsPerPage = 4;
    public const int WordsPerRow = 8;
    public const int HeaderWords = 8;

    public static (ushort RollNumber, ushort PageNumber, ushort RefreshCounter)
        ParseRequest(ReadOnlySpan<ushort> words)
    {
        if (words.Length != RequestWordCount)
            throw new ProtocolException(
                $"History request must contain {RequestWordCount} words, got {words.Length}.");

        return (words[0], words[1] == 0 ? (ushort)1 : words[1], words[2]);
    }

    public static ushort[] EncodeResponse(
        ushort status,
        ushort rollNumber,
        ushort pageNumber,
        ushort pageCount,
        ushort totalRows,
        IReadOnlyList<HistoryBufferRow> rows)
    {
        if (rows.Count > RowsPerPage)
            throw new ArgumentOutOfRangeException(nameof(rows));

        var words = new ushort[ResponseWordCount];
        words[0] = Signature;
        words[1] = Version;
        words[2] = status;
        words[3] = rollNumber;
        words[4] = pageNumber;
        words[5] = pageCount;
        words[6] = totalRows;
        words[7] = checked((ushort)rows.Count);

        for (int index = 0; index < rows.Count; index++)
        {
            HistoryBufferRow row = rows[index];
            int offset = HeaderWords + index * WordsPerRow;
            ushort[] id = EventProtocol.SplitUInt32(row.Id);
            ushort[] length = EventProtocol.SplitUInt32(row.LengthX10);
            words[offset] = id[0];
            words[offset + 1] = id[1];
            words[offset + 2] = length[0];
            words[offset + 3] = length[1];
            words[offset + 4] = row.EventType;
            words[offset + 5] = row.ShiftNumber;
            words[offset + 6] = row.LocalHour;
            words[offset + 7] = row.LocalMinute;
        }

        return words;
    }
}
