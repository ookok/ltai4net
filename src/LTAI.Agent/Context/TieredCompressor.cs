namespace LTAI.Agent.Context;

public enum CompressTier
{
    LowPriority,
    Normal,
    Recent,
    Critical
}

public sealed class TieredCompressor
{
    public CompressTier Classify(int index, int totalCount)
    {
        if (totalCount <= 5) return CompressTier.Critical;
        var pct = (double)index / totalCount;
        return pct switch
        {
            < 0.25 => CompressTier.Critical,
            < 0.50 => CompressTier.Recent,
            < 0.75 => CompressTier.Normal,
            _ => CompressTier.LowPriority
        };
    }

    public double GetCompressionRatio(CompressTier tier) => tier switch
    {
        CompressTier.LowPriority => 0.3,
        CompressTier.Normal => 0.5,
        CompressTier.Recent => 0.7,
        CompressTier.Critical => 0.95,
        _ => 0.6
    };

    public string SummarizeTierStats(int totalMessages, int compressedCount, int lowPriorityCount)
    {
        return $"压缩统计: {compressedCount}/{totalMessages} 条消息 | " +
               $"低优先级: {lowPriorityCount} | " +
               $"目标压缩率: 低优先级30% / 普通50% / 近期70% / 关键95%";
    }
}
