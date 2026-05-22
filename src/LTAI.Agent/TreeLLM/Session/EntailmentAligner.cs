using LTAI.Core.System;
using LTAI.Knowledge.Core;

namespace LTAI.Agent.Session;

public sealed record EntailmentAlignedStep(
    int Index,
    DisclosureAction Action,
    string Content,
    double EntailmentScore);

public sealed class EntailmentAligner
{
    private const double EntailmentThreshold = 0.6;
    private const int MinBlockSize = 20;

    private readonly AgenticRAG? _agenticRAG;

    public EntailmentAligner(AgenticRAG? agenticRAG = null)
    {
        _agenticRAG = agenticRAG;
    }

    public List<EntailmentAlignedStep> BuildInterleavedTrajectory(
        InteractionTrajectory trajectory)
    {
        if (trajectory.Steps.Count == 0)
            return new();

        var interleaved = new List<EntailmentAlignedStep>();
        string cumulativeReasoning = "";
        string cumulativeAnswer = "";
        int disclosedUpTo = 0;
        bool disclosedAll = false;

        for (int i = 0; i < trajectory.Steps.Count; i++)
        {
            var step = trajectory.Steps[i];

            cumulativeReasoning += " " + step.Thought;

            if (step.Observation != null)
                cumulativeAnswer += " " + step.Observation;

            interleaved.Add(new EntailmentAlignedStep(
                i, DisclosureAction.Think, step.Thought,
                ComputeEntailmentScore(cumulativeReasoning, step.Thought)));

            if (disclosedAll)
                continue;

            var answerFragments = BuildAnswerFragments(cumulativeAnswer);
            int newDisclosed = disclosedUpTo;

            for (int a = disclosedUpTo; a < answerFragments.Count; a++)
            {
                var frag = answerFragments[a];
                var entailment = ComputeEntailmentScore(cumulativeReasoning, frag);

                if (entailment >= EntailmentThreshold)
                {
                    interleaved.Add(new EntailmentAlignedStep(
                        i + a + 1000, DisclosureAction.Speak,
                        frag, entailment));
                    newDisclosed = a + 1;
                }
                else
                {
                    break;
                }
            }

            disclosedUpTo = newDisclosed;

            if (disclosedUpTo >= answerFragments.Count && trajectory.Completed)
                disclosedAll = true;
        }

        if (trajectory.Completed && disclosedUpTo < BuildAnswerFragments(cumulativeAnswer).Count)
        {
            var remainingFrags = BuildAnswerFragments(cumulativeAnswer)
                .Skip(disclosedUpTo);
            foreach (var frag in remainingFrags)
            {
                interleaved.Add(new EntailmentAlignedStep(
                    9999, DisclosureAction.Speak, frag, 1.0));
            }
        }

        return interleaved;
    }

    public List<AgentStep> ApplyDisclosurePolicy(
        InteractionTrajectory trajectory,
        List<EntailmentAlignedStep> aligned)
    {
        var result = new List<AgentStep>();

        foreach (var step in trajectory.Steps)
        {
            var alignedThink = aligned.FirstOrDefault(a =>
                a.Action == DisclosureAction.Think &&
                a.Index == step.StepIndex);

            var disclosure = alignedThink != null
                ? alignedThink.Action
                : DisclosureAction.Think;

            result.Add(step with { Disclosure = disclosure });
        }

        var speakCount = aligned.Count(a => a.Action == DisclosureAction.Speak);
        var speakFrags = aligned.Where(a => a.Action == DisclosureAction.Speak).ToList();

        for (int i = 0; i < Math.Min(speakFrags.Count, result.Count); i++)
        {
            result[i] = result[i] with
            {
                Observation = $"[DISCLOSED] {speakFrags[i].Content}",
                Disclosure = DisclosureAction.Speak
            };
        }

        return result;
    }

    public static double ComputeEntailmentScore(string reasoning, string answer)
    {
        if (string.IsNullOrEmpty(reasoning) || string.IsNullOrEmpty(answer))
            return 0;

        var rWords = Tokenize(reasoning);
        var aWords = Tokenize(answer);

        if (rWords.Length == 0 || aWords.Length == 0)
            return 0;

        var overlap = aWords.Intersect(rWords).Count();
        double keywordRatio = (double)overlap / Math.Max(1, aWords.Length);

        double lengthRatio = Math.Min(1.0, (double)rWords.Length / Math.Max(1, aWords.Length * 2));

        double entailment = keywordRatio * 0.7 + lengthRatio * 0.3;

        return Math.Clamp(entailment, 0, 1);
    }

    public List<DisclosureActionResult> ComputeDisclosureDecisions(
        InteractionTrajectory trajectory)
    {
        var results = new List<DisclosureActionResult>();
        string cumulativeReasoning = "";

        for (int i = 0; i < trajectory.Steps.Count; i++)
        {
            var step = trajectory.Steps[i];
            cumulativeReasoning += " " + step.Thought;

            var observation = step.Observation ?? "";
            var entailment = ComputeEntailmentScore(cumulativeReasoning, observation);

            bool shouldDisclose = entailment >= EntailmentThreshold
                && observation.Length >= MinBlockSize;

            results.Add(new DisclosureActionResult(
                i,
                shouldDisclose ? DisclosureAction.Speak : DisclosureAction.Think,
                observation,
                entailment,
                shouldDisclose));
        }

        return results;
    }

    private static List<string> BuildAnswerFragments(string cumulativeAnswer)
    {
        var fragments = new List<string>();
        var sentences = cumulativeAnswer.Split(
            new[] { ". ", ".\n", "。", "\n\n" },
            StringSplitOptions.RemoveEmptyEntries);

        string currentFrag = "";
        foreach (var s in sentences)
        {
            currentFrag += s + ". ";
            if (currentFrag.Length >= MinBlockSize * 2)
            {
                fragments.Add(currentFrag.Trim());
                currentFrag = "";
            }
        }

        if (currentFrag.Trim().Length > 0)
            fragments.Add(currentFrag.Trim());

        return fragments;
    }

    private static string[] Tokenize(string text)
    {
        return text.ToLowerInvariant()
            .Split(new[] { ' ', '\n', '\r', '\t', '.', ',', '，', '。', ':', ';', '！', '!', '?', '？', '(', ')', '[', ']' },
                StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 2)
            .Distinct()
            .ToArray();
    }
}
