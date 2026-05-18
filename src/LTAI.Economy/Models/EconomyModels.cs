using System.Text.Json.Serialization;

namespace LTAI.Economy.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum Metabolite { ATP, Glucose, Oxygen, Nadph }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OperationType { Basal, Active, Peak }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ComplianceLevel { Strict, Normal, Permissive }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RewardType { BinarySuccess, QualityScore, HumanFeedback, BudgetCompliance, Latency, FormatValidity, SafetyCheck }

public record MetabolicCost
{
    public string OrganName { get; init; } = "";
    public OperationType Operation { get; init; }
    public double ATP { get; set; }
    public double Glucose { get; set; }
    public double OxygenMb { get; set; }
    public double Nadph { get; set; }
    public double Intensity { get; set; } = 1.0;
    public double Timestamp { get; set; }

    public double TotalEnergy => ATP * 1.0 + Glucose * 0.8 + OxygenMb * 0.01 + Nadph * 2.0;
}

public class OrganMetabolism
{
    public string OrganName { get; init; } = "";
    public double BasalRate { get; set; }
    public double ActiveRate { get; set; }
    public double CurrentConsumption { get; set; }
    public double AtpPerSecond { get; set; }
    public double GlucosePerRequest { get; set; }
    public double OxygenMb { get; set; }
    public double TotalAtpSpent { get; set; }
    public int RequestCount { get; set; }
    public bool Suppressed { get; set; }
    public int Priority { get; set; } = 5;

    public double ActiveDraw => BasalRate * ActiveRate;
    public double BasalDraw => BasalRate;
}

public class MetabolicState
{
    public double TotalATP { get; set; } = 500.0;
    public double TotalGlucose { get; set; } = 1_000_000.0;
    public double TotalOxygenMb { get; set; } = 4096.0;
    public double CurrentTemperature { get; set; } = 0.3;
    public double StarvationLevel { get; set; }
    public bool Ketosis { get; set; }
    public double CumulativeAtpSpent { get; set; }
    public double CumulativeGlucoseSpent { get; set; }
    public double DailyCostYuan { get; set; }
    public double LastUpdated { get; set; }
    public double BudgetDayStart { get; set; }
}

public record TrilemmaVector
{
    public double CostScore { get; init; } = 0.5;
    public double SpeedScore { get; init; } = 0.5;
    public double QualityScore { get; init; } = 0.5;

    public bool Dominates(TrilemmaVector other) =>
        CostScore >= other.CostScore && SpeedScore >= other.SpeedScore &&
        QualityScore >= other.QualityScore &&
        (CostScore > other.CostScore || SpeedScore > other.SpeedScore || QualityScore > other.QualityScore);

    public double WeightedScore(EconomicPolicy? policy = null)
    {
        if (policy == null) return (CostScore + SpeedScore + QualityScore) / 3.0;
        return CostScore * policy.CostWeight + SpeedScore * policy.SpeedWeight + QualityScore * policy.QualityWeight;
    }

    public static TrilemmaVector FromRaw(double estimatedCostYuan, double estimatedMs,
        double predictedQuality, double budgetYuan = 20.0, double timeoutMs = 120000.0)
    {
        return new TrilemmaVector
        {
            CostScore = Math.Max(0, 1.0 - Math.Min(estimatedCostYuan / budgetYuan, 1.0)),
            SpeedScore = Math.Max(0, 1.0 - Math.Min(estimatedMs / timeoutMs, 1.0)),
            QualityScore = Math.Clamp(predictedQuality, 0, 1)
        };
    }
}

public class EconomicPolicy
{
    private double _costWeight = 0.33;
    private double _speedWeight = 0.33;
    private double _qualityWeight = 0.34;

    public double CostWeight
    {
        get => _costWeight; set { _costWeight = value; Normalize(); }
    }
    public double SpeedWeight
    {
        get => _speedWeight; set { _speedWeight = value; Normalize(); }
    }
    public double QualityWeight
    {
        get => _qualityWeight; set { _qualityWeight = value; Normalize(); }
    }

    public double MinScore { get; set; } = 0.3;
    public double MaxDailyBudgetYuan { get; set; } = 50.0;
    public double MaxTaskBudgetYuan { get; set; } = 10.0;
    public double MinQualityThreshold { get; set; } = 0.4;
    public ComplianceLevel ComplianceLevel { get; set; } = ComplianceLevel.Normal;
    public bool DegradationEnabled { get; set; } = true;
    public double RoiThreshold { get; set; } = 0.5;

    private void Normalize()
    {
        var sum = _costWeight + _speedWeight + _qualityWeight;
        if (sum > 0) { _costWeight /= sum; _speedWeight /= sum; _qualityWeight /= sum; }
    }

    public static EconomicPolicy Balanced() => new() { CostWeight = 0.33, SpeedWeight = 0.33, QualityWeight = 0.34, MaxDailyBudgetYuan = 50, MaxTaskBudgetYuan = 10, MinQualityThreshold = 0.4, RoiThreshold = 0.5 };
    public static EconomicPolicy Economy() => new() { CostWeight = 0.60, SpeedWeight = 0.15, QualityWeight = 0.25, MaxDailyBudgetYuan = 20, MaxTaskBudgetYuan = 3, MinQualityThreshold = 0.35, RoiThreshold = 0.3 };
    public static EconomicPolicy Quality() => new() { CostWeight = 0.15, SpeedWeight = 0.15, QualityWeight = 0.70, MaxDailyBudgetYuan = 100, MaxTaskBudgetYuan = 30, MinQualityThreshold = 0.7, RoiThreshold = 0.4 };
    public static EconomicPolicy Speed() => new() { CostWeight = 0.15, SpeedWeight = 0.70, QualityWeight = 0.15, MaxDailyBudgetYuan = 30, MaxTaskBudgetYuan = 5, MinQualityThreshold = 0.3, RoiThreshold = 0.3 };
}

public record ROIResult
{
    public string TaskId { get; init; } = "";
    public double TaskValue { get; set; }
    public double EstimatedCostYuan { get; set; }
    public double ActualCostYuan { get; set; }
    public double RoiEstimate { get; set; }
    public double RoiActual { get; set; }
    public TrilemmaVector? Trilemma { get; set; }
    public double Score { get; set; }
    public bool Approved { get; set; }
    public string Reason { get; set; } = "";
}

public record ComplianceResult
{
    public bool Passed { get; init; }
    public List<string> Checks { get; init; } = new();
    public List<string> Violations { get; init; } = new();
    public string RiskLevel { get; init; } = "low";
    public bool RequiresApproval { get; init; }
    public string Notes { get; init; } = "";
}

public record EconomicDecision
{
    public string TaskId { get; init; } = "";
    public string TaskDesc { get; init; } = "";
    public bool Go { get; init; }
    public EconomicPolicy? Policy { get; set; }
    public string SelectedModel { get; set; } = "";
    public TrilemmaVector? Trilemma { get; set; }
    public ROIResult? Roi { get; set; }
    public ComplianceResult? Compliance { get; set; }
    public int EstimatedTokens { get; init; }
    public double EstimatedCostYuan { get; init; }
    public double EstimatedMs { get; init; }
    public string Suggestion { get; set; } = "";
    public double DecidedAt { get; init; }
}

public class ThermalState
{
    public double Temperature { get; set; } = 0.5;
    public double Entropy { get; set; } = 0.5;
    public double Pressure { get; set; } = 0.5;
    public double RemainingBudget { get; set; } = 50.0;
    public double HeatFlow { get; set; }
    public double DiffusionCoefficient { get; set; }
    public double EquilibriumTemp { get; set; } = 0.4;
    public double Timestamp { get; set; }
}

public record ThermoDecision
{
    public string TaskId { get; init; } = "";
    public bool Proceed { get; init; }
    public string ModelTier { get; init; } = "flash";
    public double AllocatedBudget { get; init; }
    public double TemperatureNow { get; init; }
    public double EntropyNow { get; init; }
    public double EntropyAfter { get; init; }
    public double FreeEnergy { get; init; }
    public string Recommendation { get; init; } = "";
}

public record PreferenceSignal
{
    public string SignalType { get; init; } = "";
    public string Context { get; init; } = "";
    public string InferredPreference { get; init; } = "";
    public double Confidence { get; init; }
    public double Timestamp { get; init; }
}

// GRPO-related types

public record LatentGRPOResult
{
    public int RoundId { get; set; }
    public List<Dictionary<string, double>> InputFeatures { get; set; } = new();
    public List<double> LatentZ { get; set; } = new();
    public List<double> Advantages { get; set; } = new();
    public List<double> LatentPolicyUpdate { get; set; } = new();
    public double ReconstructionLoss { get; set; }
    public double KlDivergence { get; set; }
    public double Convergence { get; set; }
}

public record SpatialContext
{
    public int EntityCount { get; init; }
    public int HyperedgeCount { get; init; }
    public double AvgCentrality { get; init; }
    public double GraphDensity { get; init; }
    public int PrecedenceDepth { get; init; }
    public double GravityFieldEntropy { get; init; }
    public double BoundaryDistance { get; init; }
    public bool HasCycles { get; init; }
}

public record SpatialReward
{
    public string StepId { get; init; } = "";
    public double TotalReward { get; init; }
    public double BaselineReward { get; init; }
    public double SpatialDelta { get; init; }
    public double ProvidersInvolved { get; init; }
    public int SpatialFeaturesUsed { get; init; }
    public DateTime Timestamp { get; init; }
}

public record SGRPOResult
{
    public int RoundId { get; init; }
    public List<SpatialReward> Decisions { get; init; } = new();
    public double AvgSpatialDelta { get; set; }
    public string BestDecisionId { get; set; } = "";
    public List<double> SpatialPolicyUpdate { get; set; } = new();
    public double ConvergenceScore { get; set; }
}

public record PerStepReward
{
    public int StepIndex { get; init; }
    public string StepName { get; init; } = "";
    public double RawReward { get; init; }
    public double SurrogateEstimate { get; set; }
    public double ContributionWeight { get; init; }
    public Dictionary<string, double> StepContext { get; init; } = new();
    public RewardType RewardType { get; init; }
}

public record TrajectoryReward
{
    public string TrajectoryId { get; init; } = "";
    public List<PerStepReward> Steps { get; init; } = new();
    public double TotalReward { get; set; }
    public double TotalSurrogate { get; set; }
    public List<RewardType> RewardTypesUsed { get; init; } = new();
    public int TrajectoryLength { get; set; }
    public DateTime Timestamp { get; init; }
}

public record TDMOptimizationResult
{
    public int RoundId { get; init; }
    public double SurrogateLoss { get; set; }
    public double PolicyGradientNorm { get; set; }
    public double RewardImprovement { get; set; }
    public Dictionary<string, double> BestConfig { get; set; } = new();
    public double ConvergenceScore { get; set; }
    public Dictionary<string, double> PerStageContributions { get; set; } = new();
}
