namespace LTAI.Core.System;

public sealed record AgentSequence(
    string PropagatorName,
    List<string> AuditorNames,
    int PermutationIndex);

public sealed record MitigatedSequence(
    List<AgentSequence> Sequences,
    int TotalPermutations,
    double PositionEntropy,
    Dictionary<string, double> AvgPositionWeights);

public sealed class LeadAnchorMitigator
{
    private readonly Random _rng = new();

    public MitigatedSequence GenerateMitigatedSequences(
        string propagatorName,
        List<string> auditorNames,
        int? maxPermutations = null)
    {
        var allPermutations = GeneratePermutations(auditorNames);
        int total = allPermutations.Count;

        var sequences = new List<AgentSequence>();
        int permLimit = maxPermutations ?? Math.Min(total, 6);

        var used = new HashSet<string>();
        for (int i = 0; i < Math.Min(permLimit, total); i++)
        {
            string idx = string.Join(",", allPermutations[i]);
            if (used.Add(idx))
            {
                sequences.Add(new AgentSequence(
                    propagatorName,
                    allPermutations[i],
                    i));
            }
        }

        double positionEntropy = ComputePositionEntropy(sequences, auditorNames);
        var avgWeights = ComputeAvgPositionWeights(sequences, propagatorName);

        return new MitigatedSequence(
            sequences,
            total,
            Math.Round(positionEntropy, 3),
            avgWeights);
    }

    public List<AgentSequence> GenerateRotatingLeadAnchor(
        string propagatorName,
        List<string> auditorNames,
        int rounds)
    {
        var sequences = new List<AgentSequence>();

        for (int round = 0; round < rounds; round++)
        {
            var rotated = auditorNames
                .Skip(round % auditorNames.Count)
                .Concat(auditorNames.Take(round % auditorNames.Count))
                .ToList();

            sequences.Add(new AgentSequence(propagatorName, rotated, round));
        }

        return sequences;
    }

    public List<string> ShuffleOrder(List<string> auditorNames)
    {
        return auditorNames.OrderBy(_ => _rng.Next()).ToList();
    }

    public Dictionary<string, double> EvaluateAnchorBias(
        AgentSequence sequence,
        SocialLoadModel loadModel,
        string propagatorFamily,
        double taskEntropy)
    {
        var bias = new Dictionary<string, double>();
        var auditors = sequence.AuditorNames.Select(n =>
            new AuditorIdentity(n, "default", 0.5, 1.0)).ToList();

        for (int position = 1; position <= sequence.AuditorNames.Count; position++)
        {
            var permuted = sequence.AuditorNames
                .Skip(position - 1)
                .Concat(sequence.AuditorNames.Take(position - 1))
                .ToList();

            var permutedAuditors = permuted.Select(n =>
                new AuditorIdentity(n, "default", 0.5, 1.0)).ToList();

            var result = loadModel.Evaluate(
                propagatorFamily, permutedAuditors, taskEntropy);

            bias[$"pos_{position}"] = Math.Round(result.Sovereignty, 3);
        }

        return bias;
    }

    public bool IsLeadAnchorVulnerable(
        AgentSequence sequence,
        SocialLoadModel loadModel,
        string propagatorFamily,
        double taskEntropy)
    {
        var original = loadModel.Evaluate(
            propagatorFamily,
            sequence.AuditorNames.Select(n =>
                new AuditorIdentity(n, "default", 0.5, 1.0)).ToList(),
            taskEntropy);

        var reversed = new AgentSequence(
            sequence.PropagatorName,
            Enumerable.Reverse(sequence.AuditorNames).ToList(),
            sequence.PermutationIndex + 1000);

        var reversedResult = loadModel.Evaluate(
            propagatorFamily,
            reversed.AuditorNames.Select(n =>
                new AuditorIdentity(n, "default", 0.5, 1.0)).ToList(),
            taskEntropy);

        return Math.Abs(original.Sovereignty - reversedResult.Sovereignty) > 0.1;
    }

    private static List<List<string>> GeneratePermutations(List<string> items)
    {
        var result = new List<List<string>>();
        if (items.Count == 0) return result;
        if (items.Count == 1)
        {
            result.Add(new List<string>(items));
            return result;
        }

        for (int i = 0; i < items.Count; i++)
        {
            var current = items[i];
            var remaining = items.Where((_, idx) => idx != i).ToList();
            var subPerms = GeneratePermutations(remaining);

            foreach (var sub in subPerms)
            {
                var perm = new List<string> { current };
                perm.AddRange(sub);
                result.Add(perm);
            }
        }

        return result;
    }

    private static double ComputePositionEntropy(
        List<AgentSequence> sequences,
        List<string> auditorNames)
    {
        double entropy = 0;

        for (int pos = 0; pos < auditorNames.Count; pos++)
        {
            var posDist = new Dictionary<string, int>();
            foreach (var seq in sequences)
            {
                if (pos < seq.AuditorNames.Count)
                {
                    var name = seq.AuditorNames[pos];
                    posDist[name] = posDist.TryGetValue(name, out var c) ? c + 1 : 1;
                }
            }

            double posEntropy = 0;
            double total = posDist.Values.Sum();
            foreach (var count in posDist.Values)
            {
                double p = count / total;
                if (p > 0)
                    posEntropy -= p * Math.Log2(p);
            }

            entropy += posEntropy;
        }

        return entropy / Math.Max(1, auditorNames.Count);
    }

    private static Dictionary<string, double> ComputeAvgPositionWeights(
        List<AgentSequence> sequences,
        string propagatorName)
    {
        var weights = new Dictionary<string, double>();
        foreach (var seq in sequences)
        {
            for (int i = 0; i < seq.AuditorNames.Count; i++)
            {
                var name = seq.AuditorNames[i];
                double posWeight = SocialLoadModel.ComputePositionWeight(i, seq.AuditorNames.Count);

                if (weights.TryGetValue(name, out var current))
                    weights[name] = (current + posWeight) / 2;
                else
                    weights[name] = posWeight;
            }
        }
        return weights;
    }
}
