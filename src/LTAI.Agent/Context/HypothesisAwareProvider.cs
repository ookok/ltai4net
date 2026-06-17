using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace LTAI.Agent.Context;

public sealed class HypothesisAwareProvider : AIContextProvider
{
    private readonly string _hypothesis;

    public HypothesisAwareProvider(string hypothesis)
        : base(null, null, null)
    {
        _hypothesis = hypothesis;
    }

    public override IReadOnlyList<string> StateKeys => ["Hypothesis"];

    public string Hypothesis => _hypothesis;

    protected override ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_hypothesis))
            return ValueTask.FromResult(new AIContext());

        var text = $"""
            <hypothesis>
            当前分支的假设/关注点:
            {_hypothesis}

            请专门验证或探索这个方向。不要被其他分支的假设干扰。
            如果该方向证据不足，请明确说明；如果证实，记录关键发现。
            </hypothesis>
            """;

        return ValueTask.FromResult(new AIContext
        {
            Messages = [new ChatMessage(ChatRole.System, text)]
        });
    }
}

public sealed class HypothesisRouterContext
{
    private readonly List<(string Hypothesis, AIContextProvider Provider)> _branches = [];

    public int BranchCount => _branches.Count;

    public HypothesisRouterContext AddBranch(string hypothesis)
    {
        var provider = new HypothesisAwareProvider(hypothesis);
        _branches.Add((hypothesis, provider));
        return this;
    }

    public IReadOnlyList<(string Hypothesis, AIContextProvider Provider)> GetBranches()
        => _branches.AsReadOnly();

    public static Builder Create() => new();

    public sealed class Builder
    {
        private readonly List<string> _hypotheses = [];

        public Builder Add(string hypothesis)
        {
            _hypotheses.Add(hypothesis);
            return this;
        }

        public HypothesisRouterContext Build()
        {
            var ctx = new HypothesisRouterContext();
            foreach (var h in _hypotheses)
                ctx.AddBranch(h);
            return ctx;
        }
    }
}
