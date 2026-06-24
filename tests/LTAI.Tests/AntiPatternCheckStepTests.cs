using LTAI.Agent.Pipeline;
using LTAI.Agent.Pipeline.Steps;
using Microsoft.Extensions.AI;
using Xunit;

namespace LTAI.Tests;

public sealed class AntiPatternCheckStepTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "LTAI_AntiPattern_" + Guid.NewGuid().ToString("N"));
    private readonly AntiPatternCheckStep _step = new();

    public AntiPatternCheckStepTests() => Directory.CreateDirectory(_tempDir);
    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task CleanText_NoPatterns()
    {
        var ctx = new MessageContext("write a function", CancellationToken.None);
        ctx.Messages.Add(new(ChatRole.Assistant,
            "Here is a C# function:\n```csharp\nint Add(int a, int b) => a + b;\n```"));
        ctx = await _step.ProcessAsync(ctx);

        Assert.False(ctx.AntiPatternBlocked);
        Assert.False(ctx.TryGet<List<AntiPattern>>("AntiPatterns", out _));
    }

    [Fact]
    public async Task EmojiAbuse_Detected()
    {
        var ctx = new MessageContext("write code", CancellationToken.None);
        ctx.Messages.Add(new(ChatRole.Assistant,
            "Here is your code: \u2B50\u2B50\u2B50\u2600\u2600\u2600 check it out!"));
        ctx = await _step.ProcessAsync(ctx);

        Assert.False(ctx.AntiPatternBlocked);
        Assert.True(ctx.TryGet<List<AntiPattern>>("AntiPatterns", out var patterns));
        Assert.Contains(patterns, p => p.Pattern == "emoji_abuse");
    }

    [Fact]
    public async Task AiOpening_Detected()
    {
        var ctx = new MessageContext("review this", CancellationToken.None);
        ctx.Messages.Add(new(ChatRole.Assistant,
            "## Let me review your code and provide feedback."));
        ctx = await _step.ProcessAsync(ctx);

        Assert.True(ctx.TryGet<List<AntiPattern>>("AntiPatterns", out var patterns));
        Assert.Contains(patterns, p => p.Pattern == "ai_opening");
    }

    [Fact]
    public async Task TemplatePlaceholder_Detected()
    {
        var ctx = new MessageContext("write template", CancellationToken.None);
        ctx.Messages.Add(new(ChatRole.Assistant,
            "Your API key is {{API_KEY}}. Replace it with your actual key."));
        ctx = await _step.ProcessAsync(ctx);

        Assert.True(ctx.TryGet<List<AntiPattern>>("AntiPatterns", out var patterns));
        Assert.Contains(patterns, p => p.Pattern == "template_placeholder");
    }

    [Fact]
    public async Task TodoFixme_Detected()
    {
        var ctx = new MessageContext("write code", CancellationToken.None);
        ctx.Messages.Add(new(ChatRole.Assistant,
            "// TODO: implement this method\n// FIXME: handle edge case"));
        ctx = await _step.ProcessAsync(ctx);

        Assert.True(ctx.TryGet<List<AntiPattern>>("AntiPatterns", out var patterns));
        Assert.Contains(patterns, p => p.Pattern == "todo_fixme");
    }

    [Fact]
    public async Task HedgeLanguage_Detected()
    {
        var ctx = new MessageContext("explain", CancellationToken.None);
        ctx.Messages.Add(new(ChatRole.Assistant,
            "I think this might work, but I'm not sure about the performance."));
        ctx = await _step.ProcessAsync(ctx);

        Assert.True(ctx.TryGet<List<AntiPattern>>("AntiPatterns", out var patterns));
        Assert.Contains(patterns, p => p.Pattern == "hedge_language");
    }

    [Fact]
    public async Task CourtesyOveruse_Detected()
    {
        var ctx = new MessageContext("help", CancellationToken.None);
        ctx.Messages.Add(new(ChatRole.Assistant,
            "I'd be happy to help you with that!"));
        ctx = await _step.ProcessAsync(ctx);

        Assert.True(ctx.TryGet<List<AntiPattern>>("AntiPatterns", out var patterns));
        Assert.Contains(patterns, p => p.Pattern == "ai_courtesy");
    }

    [Fact]
    public async Task HardcodedSecret_Detected()
    {
        var ctx = new MessageContext("write config", CancellationToken.None);
        ctx.Messages.Add(new(ChatRole.Assistant,
            @"api_key = ""sk-abcdefghijklmnopqrstuvwxyz1234567890"""));
        ctx = await _step.ProcessAsync(ctx);

        Assert.True(ctx.TryGet<List<AntiPattern>>("AntiPatterns", out var patterns));
        Assert.Contains(patterns, p => p.Pattern is "hardcoded_secret" or "hardcoded_api_key");
    }

    [Fact]
    public async Task GradianCliche_Detected()
    {
        var ctx = new MessageContext("design UI", CancellationToken.None);
        ctx.Messages.Add(new(ChatRole.Assistant,
            "Use a purple to pink gradient for the background"));
        ctx = await _step.ProcessAsync(ctx);

        Assert.True(ctx.TryGet<List<AntiPattern>>("AntiPatterns", out var patterns));
        Assert.Contains(patterns, p => p.Pattern == "gradient_cliche");
    }

    [Fact]
    public async Task GlobalStylesObject_Detected()
    {
        var ctx = new MessageContext("create style", CancellationToken.None);
        ctx.Messages.Add(new(ChatRole.Assistant,
            "const styles = {\n  container: { flex: 1 },\n  title: { color: 'red' }\n}"));
        ctx = await _step.ProcessAsync(ctx);

        Assert.True(ctx.TryGet<List<AntiPattern>>("AntiPatterns", out var patterns));
        Assert.Contains(patterns, p => p.Pattern == "global_styles_object");
    }

    [Fact]
    public async Task ScrollIntoView_Detected()
    {
        var ctx = new MessageContext("scroll", CancellationToken.None);
        ctx.Messages.Add(new(ChatRole.Assistant,
            "element.scrollIntoView({ behavior: 'smooth' })"));
        ctx = await _step.ProcessAsync(ctx);

        Assert.True(ctx.TryGet<List<AntiPattern>>("AntiPatterns", out var patterns));
        Assert.Contains(patterns, p => p.Pattern == "scroll_into_view");
    }
}
