namespace LTAI.Core.Governors;

public sealed record GenePoolConfig
{
    public int MaxPopulation { get; init; } = 200;
    public int ShareInterval { get; init; } = 5;
    public int EliteCount { get; init; } = 5;
    public int CrossoverCount { get; init; } = 10;
    public int MutateCount { get; init; } = 15;
    public double PlateauWindow { get; init; } = 10;
    public double PlateauFitnessThreshold { get; init; } = 0.95;
    public double PlateauTolerance { get; init; } = 0.01;
    public int HistoryMaxSize { get; init; } = 50;
}
