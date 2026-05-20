namespace LTAI.Core.System;

public sealed record AuditorIdentity(
    string Name,
    string Family,
    double BaseAuthority,
    double KinshipCoefficient);

public sealed record SocialLoadResult(
    double CompositeLoad,
    double Sovereignty,
    int InteractionDepthLimit,
    bool IsCollapsed,
    List<AuditorContribution> AuditorContributions);

public sealed record AuditorContribution(
    string AuditorName,
    int Position,
    double PositionWeight,
    double Authority,
    double Kinship,
    double Contribution);

public sealed class SocialLoadModel
{
    private const double DefaultPropagatorResilience = 1.0;
    private const double SovereigntyThreshold = 0.5;

    private readonly Dictionary<string, double> _familyResilience = new()
    {
        ["claude"] = 5.0,
        ["gemini"] = 1.5,
        ["gpt"] = 1.0,
        ["qwen"] = 1.2,
        ["default"] = 1.0
    };

    private readonly Dictionary<string, double> _baseAuthority = new()
    {
        ["claude"] = 0.9,
        ["gemini"] = 0.75,
        ["gpt"] = 0.7,
        ["qwen"] = 0.65,
        ["default"] = 0.5
    };

    public SocialLoadResult Evaluate(
        string propagatorFamily,
        List<AuditorIdentity> auditors,
        double taskEntropy,
        double baselineSovereignty = 1.0)
    {
        double resilience = _familyResilience.TryGetValue(propagatorFamily.ToLowerInvariant(), out var r)
            ? r : _familyResilience["default"];

        double totalLoad = 0;
        var contributions = new List<AuditorContribution>();

        for (int i = 0; i < auditors.Count; i++)
        {
            var a = auditors[i];
            double posWeight = ComputePositionWeight(i, auditors.Count);
            double auth = _baseAuthority.TryGetValue(a.Family.ToLowerInvariant(), out var ba)
                ? ba : _baseAuthority["default"];
            double kinship = a.Family.Equals(propagatorFamily, StringComparison.OrdinalIgnoreCase)
                ? 0.3 : 1.0;

            double contribution = posWeight * auth * kinship;
            totalLoad += contribution;

            contributions.Add(new AuditorContribution(
                a.Name, i + 1, Math.Round(posWeight, 3),
                Math.Round(auth, 3), Math.Round(kinship, 3),
                Math.Round(contribution, 3)));
        }

        double sovereignty = baselineSovereignty *
            Math.Exp(-taskEntropy / resilience * totalLoad);

        sovereignty = Math.Clamp(sovereignty, 0, 1);

        int depthLimit = ComputeInteractionDepthLimit(
            resilience, taskEntropy, baselineSovereignty, contributions);

        return new SocialLoadResult(
            Math.Round(totalLoad, 3),
            Math.Round(sovereignty, 3),
            depthLimit,
            sovereignty < SovereigntyThreshold,
            contributions);
    }

    public int ComputeInteractionDepthLimit(
        double resilience,
        double taskEntropy,
        double baselineSovereignty,
        List<AuditorContribution> contributions)
    {
        double threshold = resilience / Math.Max(0.1, taskEntropy) *
            Math.Log(2 * Math.Max(0.1, baselineSovereignty));

        double accumulated = 0;
        for (int i = 0; i < contributions.Count; i++)
        {
            accumulated += contributions[i].Contribution;
            if (accumulated > threshold)
                return i + 1;
        }

        return contributions.Count + 1;
    }

    public static double ComputePositionWeight(int position, int totalCount)
    {
        if (position == 0)
            return 1.0;

        return Math.Max(0.15, 1.0 / (position + 1));
    }

    public bool WouldCollapse(
        string propagatorFamily,
        List<AuditorIdentity> auditors,
        double taskEntropy)
    {
        var result = Evaluate(propagatorFamily, auditors, taskEntropy);
        return result.IsCollapsed;
    }

    public int GetSafeSwarmSize(
        string propagatorFamily,
        string auditorFamily,
        double taskEntropy)
    {
        int n = 0;
        double resilience = _familyResilience.TryGetValue(propagatorFamily.ToLowerInvariant(), out var r)
            ? r : _familyResilience["default"];

        while (true)
        {
            n++;
            var auditors = Enumerable.Range(0, n)
                .Select(i => new AuditorIdentity($"auditor_{i}", auditorFamily, 0.5, 1.0))
                .ToList();

            if (WouldCollapse(propagatorFamily, auditors, taskEntropy))
                return Math.Max(1, n - 1);

            if (n > 100) return 100;
        }
    }

    public double GetResilience(string family)
        => _familyResilience.TryGetValue(family.ToLowerInvariant(), out var r)
            ? r : _familyResilience["default"];

    public void SetResilience(string family, double resilience)
    {
        _familyResilience[family.ToLowerInvariant()] = resilience;
    }
}
