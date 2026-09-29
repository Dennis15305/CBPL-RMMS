using Cbpl.Rmms.Collector.Protocol;
using Xunit;

namespace Cbpl.Rmms.Collector.Tests;

public sealed class HistoryBufferProtocolTests
{
    [Fact]
    public void ZeroPageInRequestMeansFirstPage()
    {
        var request = HistoryBufferProtocol.ParseRequest([3, 0, 17]);

        Assert.Equal((ushort)3, request.RollNumber);
        Assert.Equal((ushort)1, request.PageNumber);
        Assert.Equal((ushort)17, request.RefreshCounter);
    }

    [Fact]
    public void ResponseUsesFourFixedRowsAndHighWordFirst()
    {
        ushort[] words = HistoryBufferProtocol.EncodeResponse(
            1, 2, 3, 5, 18,
            [new HistoryBufferRow(0x12345678, 301, 1, 2, 21, 7)]);

        Assert.Equal(HistoryBufferProtocol.ResponseWordCount, words.Length);
        Assert.Equal((ushort)0x4853, words[0]);
        Assert.Equal((ushort)1, words[1]);
        Assert.Equal((ushort)1, words[2]);
        Assert.Equal((ushort)2, words[3]);
        Assert.Equal((ushort)3, words[4]);
        Assert.Equal((ushort)5, words[5]);
        Assert.Equal((ushort)18, words[6]);
        Assert.Equal((ushort)1, words[7]);
        Assert.Equal((ushort)0x1234, words[8]);
        Assert.Equal((ushort)0x5678, words[9]);
        Assert.Equal((ushort)0, words[10]);
        Assert.Equal((ushort)301, words[11]);
        Assert.Equal((ushort)1, words[12]);
        Assert.Equal((ushort)2, words[13]);
        Assert.Equal((ushort)21, words[14]);
        Assert.Equal((ushort)7, words[15]);
        Assert.All(words[16..], word => Assert.Equal((ushort)0, word));
    }
}
