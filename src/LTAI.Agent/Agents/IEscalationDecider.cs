namespace LTAI.Agent;

public interface IEscalationDecider
{
    bool IsSimpleQuery(string message);

    int EstimateComplexity(string message);

    string ClassifyTaskType(string message);

    bool IsMultiStep(string message);

    (bool needsPro, string reason, double confidence) Evaluate(
        string message,
        string response,
        L1State l1State,
        double entropy,
        double valueOfInfo,
        bool steerJudgeSaysInadequate,
        string? steerJudgeReason);

    bool ContainsRefusalPatterns(string text);
}
