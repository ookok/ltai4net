// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  AgentEvaluationTests — MAF 框架 EvalChecks/LocatorEvaluator 集成测试
// ═══════════════════════════════════════════════════════════════
//
//  演示如何使用 MAF IAgentEvaluator + EvalChecks 评估 agent 响应质量：
//  - KeywordCheck: 响应是否包含指定关键词
//  - NonEmpty: 响应是否非空
//  - ToolCalledCheck: 是否调用了指定工具
//  - HasImageContent: 对话是否包含图片
// ═══════════════════════════════════════════════════════════════

using Xunit;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;

namespace LTAI.Tests;

public class EvalChecksTests
{
    [Fact]
    public void KeywordCheck_AllPresent_Passes()
    {
        var check = EvalChecks.KeywordCheck("hello", "world");
        var item = new EvalItem("greet me", "hello world, how are you?");
        var result = check(item);
        Assert.True(result.Passed);
        Assert.Equal("keyword_check", result.CheckName);
    }

    [Fact]
    public void KeywordCheck_Missing_Fails()
    {
        var check = EvalChecks.KeywordCheck("foo", "bar");
        var item = new EvalItem("query", "only foo is here");
        var result = check(item);
        Assert.False(result.Passed);
        Assert.Contains("bar", result.Reason);
    }

    [Fact]
    public void KeywordCheck_CaseInsensitive_ByDefault()
    {
        var check = EvalChecks.KeywordCheck("HELLO");
        var item = new EvalItem("q", "hello world");
        Assert.True(check(item).Passed);
    }

    [Fact]
    public void KeywordCheck_CaseSensitive_Missing()
    {
        var check = EvalChecks.KeywordCheck(caseSensitive: true, "HELLO");
        var item = new EvalItem("q", "hello world");
        Assert.False(check(item).Passed);
    }

    [Fact]
    public void NonEmpty_DefaultLength_Passes()
    {
        var check = EvalChecks.NonEmpty();
        Assert.True(check(new EvalItem("q", "some text")).Passed);
    }

    [Fact]
    public void NonEmpty_EmptyResponse_Fails()
    {
        var check = EvalChecks.NonEmpty();
        Assert.False(check(new EvalItem("q", "")).Passed);
    }

    [Fact]
    public void NonEmpty_MinLengthEnforced()
    {
        var check = EvalChecks.NonEmpty(minLength: 10);
        Assert.False(check(new EvalItem("q", "short")).Passed);
        Assert.True(check(new EvalItem("q", "this is long enough")).Passed);
    }

    [Fact]
    public void ContainsExpected_Match_Passes()
    {
        var item = new EvalItem("q", "the answer is 42")
        {
            ExpectedOutput = "42"
        };
        Assert.True(EvalChecks.ContainsExpected()(item).Passed);
    }

    [Fact]
    public void ContainsExpected_NoExpected_Fails()
    {
        var item = new EvalItem("q", "anything");
        Assert.False(EvalChecks.ContainsExpected()(item).Passed);
    }

    [Fact]
    public void ToolCallsPresent_NoCalls_Fails()
    {
        var conv = new List<ChatMessage>
        {
            new(ChatRole.User, "hello"),
            new(ChatRole.Assistant, "hi there")
        };
        var item = new EvalItem(conv);
        Assert.False(EvalChecks.ToolCallsPresent()(item).Passed);
    }

    [Fact]
    public void ToolCallsPresent_WithCall_Passes()
    {
        var conv = new List<ChatMessage>
        {
            new(ChatRole.User, "what's the weather?"),
            new(ChatRole.Assistant, "let me check")
            {
                Contents = [new FunctionCallContent("call_1", "get_weather", new Dictionary<string, object?> { ["city"] = "Seattle" })]
            }
        };
        var item = new EvalItem(conv);
        Assert.True(EvalChecks.ToolCallsPresent()(item).Passed);
    }

    [Fact]
    public void ToolCalledCheck_AllMode_MissingFails()
    {
        var conv = new List<ChatMessage>
        {
            new(ChatRole.Assistant, "")
            {
                Contents = [new FunctionCallContent("c1", "read_file", null)]
            }
        };
        var item = new EvalItem(conv);
        var check = EvalChecks.ToolCalledCheck("read_file", "write_file");
        Assert.False(check(item).Passed);
    }

    [Fact]
    public void ToolCalledCheck_AllMode_PresentPasses()
    {
        var conv = new List<ChatMessage>
        {
            new(ChatRole.Assistant, "")
            {
                Contents = [
                    new FunctionCallContent("c1", "read_file", null),
                    new FunctionCallContent("c2", "write_file", null)
                ]
            }
        };
        var item = new EvalItem(conv);
        var check = EvalChecks.ToolCalledCheck("read_file", "write_file");
        Assert.True(check(item).Passed);
    }

    [Fact]
    public void ToolCalledCheck_AnyMode_OnePresent()
    {
        var conv = new List<ChatMessage>
        {
            new(ChatRole.Assistant, "")
            {
                Contents = [new FunctionCallContent("c1", "read_file", null)]
            }
        };
        var item = new EvalItem(conv);
        var check = EvalChecks.ToolCalledCheck(ToolCalledMode.Any, "read_file", "write_file");
        Assert.True(check(item).Passed);
    }

    [Fact]
    public void ToolCallArgsMatch_SubsetMatch_Passes()
    {
        var conv = new List<ChatMessage>
        {
            new(ChatRole.Assistant, "")
            {
                Contents = [new FunctionCallContent("c1", "search",
                    new Dictionary<string, object?> { ["query"] = "auth", ["limit"] = 10, ["offset"] = 0 })]
            }
        };
        var item = new EvalItem(conv)
        {
            ExpectedToolCalls = [new ExpectedToolCall("search", new Dictionary<string, object> { ["query"] = "auth" })]
        };
        Assert.True(EvalChecks.ToolCallArgsMatch()(item).Passed);
    }

    [Fact]
    public void ToolCallArgsMatch_Mismatch_Fails()
    {
        var conv = new List<ChatMessage>
        {
            new(ChatRole.Assistant, "")
            {
                Contents = [new FunctionCallContent("c1", "search",
                    new Dictionary<string, object?> { ["query"] = "wrong" })]
            }
        };
        var item = new EvalItem(conv)
        {
            ExpectedToolCalls = [new ExpectedToolCall("search", new Dictionary<string, object> { ["query"] = "right" })]
        };
        Assert.False(EvalChecks.ToolCallArgsMatch()(item).Passed);
    }
}

public class LocalEvaluatorTests
{
    [Fact]
    public async Task EvaluateAsync_AllPass_ReturnsAllPassed()
    {
        var evaluator = new LocalEvaluator(
            EvalChecks.KeywordCheck("ok"),
            EvalChecks.NonEmpty()
        );

        var items = new List<EvalItem>
        {
            new("q1", "response ok"),
            new("q2", "this is also ok"),
        };

        var results = await evaluator.EvaluateAsync(items, "test run");
        Assert.Equal(2, results.Total);
        Assert.Equal(2, results.Passed);
        Assert.Equal(0, results.Failed);
        Assert.True(results.AllPassed);
    }

    [Fact]
    public async Task EvaluateAsync_OneFails_RecordsCorrectCount()
    {
        var evaluator = new LocalEvaluator(EvalChecks.KeywordCheck("required"));

        var items = new List<EvalItem>
        {
            new("q1", "this has the required word"),
            new("q2", "this one does not"),
        };

        var results = await evaluator.EvaluateAsync(items);
        Assert.Equal(1, results.Passed);
        Assert.Equal(1, results.Failed);
        Assert.False(results.AllPassed);
    }

    [Fact]
    public void AssertAllPassed_OnFailure_Throws()
    {
        var results = new AgentEvaluationResults("test", items: [
            new EvaluationResult(),
        ]);

        Assert.Throws<InvalidOperationException>(() => results.AssertAllPassed("custom msg"));
    }

    [Fact]
    public void AssertAllPassed_OnSuccess_DoesNotThrow()
    {
        var result = new EvaluationResult();
        result.Metrics["non_empty"] = new BooleanMetric("non_empty", true, reason: "ok")
        {
            Interpretation = new EvaluationMetricInterpretation { Rating = EvaluationRating.Good, Failed = false }
        };

        var results = new AgentEvaluationResults("test", items: [result]);
        results.AssertAllPassed();
    }
}

public class FunctionEvaluatorTests
{
    [Fact]
    public void Create_SimpleCheck_Works()
    {
        var check = FunctionEvaluator.Create("len_check",
            response => response.Length > 5);

        var passItem = new EvalItem("q", "long enough response");
        var failItem = new EvalItem("q", "no");

        Assert.True(check(passItem).Passed);
        Assert.False(check(failItem).Passed);
    }
}

public class EvalItemTests
{
    [Fact]
    public void Constructor_QueryResponse_BuildsConversation()
    {
        var item = new EvalItem("hello", "world");
        Assert.Equal("hello", item.Query);
        Assert.Equal("world", item.Response);
        Assert.Equal(2, item.Conversation.Count);
    }

    [Fact]
    public void Constructor_Conversation_DerivesQueryAndResponse()
    {
        var conv = new List<ChatMessage>
        {
            new(ChatRole.User, "ask me"),
            new(ChatRole.Assistant, "answer here"),
        };
        var item = new EvalItem(conv);
        Assert.Equal("ask me", item.Query);
        Assert.Equal("answer here", item.Response);
    }

    [Fact]
    public void PerTurnItems_MultipleTurns_CreatesOneItemPerTurn()
    {
        var conv = new List<ChatMessage>
        {
            new(ChatRole.User, "first question"),
            new(ChatRole.Assistant, "first answer"),
            new(ChatRole.User, "second question"),
            new(ChatRole.Assistant, "second answer"),
        };

        var items = EvalItem.PerTurnItems(conv);
        Assert.Equal(2, items.Count);
        Assert.Equal("first question", items[0].Query);
        Assert.Equal("first answer", items[0].Response);
        Assert.Equal("second question", items[1].Query);
        Assert.Equal("second answer", items[1].Response);
    }
}
