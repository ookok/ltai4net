using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Moq;
using Xunit.Abstractions;

namespace LTAI.TUI.Tests;

/// <summary>
/// Simulates what MAF ChatClientAgent does internally:
/// 1) Send messages → get back tool calls
/// 2) Execute each tool → produce FunctionResultContent
/// 3) Re-inject results → get final text
/// </summary>
public static class AgentToolCallingHelper
{
    public static async Task<ChatResponse> RunAsync(
        IChatClient client,
        List<ChatMessage> messages,
        Func<FunctionCallContent, Task<string>> executeTool,
        int maxIterations = 10)
    {
        var response = await client.GetResponseAsync(messages, null, default);
        var iteration = 0;

        while (response.FinishReason == ChatFinishReason.ToolCalls && iteration < maxIterations)
        {
            iteration++;
            var results = new List<ChatMessage>();

            foreach (var fc in response.Messages[0].Contents.OfType<FunctionCallContent>())
            {
                var result = await executeTool(fc);
                results.Add(new ChatMessage(ChatRole.Tool,
                    [new FunctionResultContent(fc.CallId, result)]));
            }

            var fullHistory = new List<ChatMessage>(messages);
            fullHistory.Add(response.Messages[0]);
            fullHistory.AddRange(results);

            response = await client.GetResponseAsync(fullHistory, null, default);
        }

        return response;
    }
}

public sealed class ToolCallMockTests
{
    // ── Streaming helpers ──

    private static IAsyncEnumerable<ChatResponseUpdate> StreamFrom(string text)
    {
        return Core(text);
        static async IAsyncEnumerable<ChatResponseUpdate> Core(string t)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, t)
            {
                Contents = [new TextContent(t)],
                FinishReason = ChatFinishReason.Stop,
            };
        }
    }

    private static IAsyncEnumerable<ChatResponseUpdate> StreamFromToolCalls(
        params (string name, Dictionary<string, object?>? args)[] calls)
    {
        return Core(calls);
        static async IAsyncEnumerable<ChatResponseUpdate> Core(
            (string name, Dictionary<string, object?>? args)[] calls)
        {
            foreach (var (name, args) in calls)
            {
                yield return new ChatResponseUpdate
                {
                    Role = ChatRole.Assistant,
                    Contents = [new FunctionCallContent(Guid.NewGuid().ToString("n"), name, args)],
                };
            }
            yield return new ChatResponseUpdate
            {
                Role = ChatRole.Assistant,
                Contents = [],
                FinishReason = ChatFinishReason.ToolCalls,
            };
        }
    }

    // ── Single tool call ──

    [Fact]
    public async Task ToolCall_Moq_ReturnsFunctionCallContent()
    {
        var mock = new Mock<IChatClient>();
        mock.Setup(x => x.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                new List<AIContent>
                {
                    new FunctionCallContent("call_1", "get_weather",
                        new Dictionary<string, object?> { ["city"] = "Paris" }),
                }))
            { FinishReason = ChatFinishReason.ToolCalls });

        var response = await mock.Object.GetResponseAsync(
            new List<ChatMessage> { new(ChatRole.User, "weather in Paris?") });

        Assert.Equal(ChatFinishReason.ToolCalls, response.FinishReason);
        var fc = Assert.Single(response.Messages[0].Contents.OfType<FunctionCallContent>());
        Assert.Equal("get_weather", fc.Name);
        Assert.Equal("Paris", fc.Arguments!["city"]);
    }

    // ── Multiple parallel tool calls ──

    [Fact]
    public async Task ToolCall_Moq_ParallelCalls()
    {
        var mock = new Mock<IChatClient>();
        mock.Setup(x => x.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                new List<AIContent>
                {
                    new FunctionCallContent("call_1", "get_weather",
                        new Dictionary<string, object?> { ["city"] = "Paris" }),
                    new FunctionCallContent("call_2", "get_weather",
                        new Dictionary<string, object?> { ["city"] = "Tokyo" }),
                }))
            { FinishReason = ChatFinishReason.ToolCalls });

        var response = await mock.Object.GetResponseAsync(
            new List<ChatMessage> { new(ChatRole.User, "weather in Paris and Tokyo?") });

        Assert.Equal(ChatFinishReason.ToolCalls, response.FinishReason);
        var calls = response.Messages[0].Contents.OfType<FunctionCallContent>().ToList();
        Assert.Equal(2, calls.Count);
        Assert.Equal("get_weather", calls[0].Name);
        Assert.Equal("Paris", calls[0].Arguments!["city"]);
        Assert.Equal("get_weather", calls[1].Name);
        Assert.Equal("Tokyo", calls[1].Arguments!["city"]);
    }

    // ── Null / empty arguments ──

    [Fact]
    public async Task ToolCall_Moq_NullArgs()
    {
        var mock = new Mock<IChatClient>();
        mock.Setup(x => x.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                new List<AIContent> { new FunctionCallContent("call_1", "ping", null!) }))
            { FinishReason = ChatFinishReason.ToolCalls });

        var response = await mock.Object.GetResponseAsync(
            new List<ChatMessage> { new(ChatRole.User, "ping") });

        Assert.Equal(ChatFinishReason.ToolCalls, response.FinishReason);
        var fc = Assert.Single(response.Messages[0].Contents.OfType<FunctionCallContent>());
        Assert.Equal("ping", fc.Name);
    }

    [Fact]
    public async Task ToolCall_Moq_EmptyArgs()
    {
        var mock = new Mock<IChatClient>();
        mock.Setup(x => x.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                new List<AIContent> { new FunctionCallContent("call_1", "ping", new Dictionary<string, object?>()) }))
            { FinishReason = ChatFinishReason.ToolCalls });

        var response = await mock.Object.GetResponseAsync(
            new List<ChatMessage> { new(ChatRole.User, "ping") });

        var fc = Assert.Single(response.Messages[0].Contents.OfType<FunctionCallContent>());
        Assert.Empty(fc.Arguments!);
    }

    // ── Tool call → text (multi-turn via SetupSequence) ──

    [Fact]
    public async Task ToolCall_Moq_ThenText()
    {
        var mock = new Mock<IChatClient>();
        mock.SetupSequence(x => x.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                new List<AIContent>
                {
                    new FunctionCallContent("call_1", "get_weather",
                        new Dictionary<string, object?> { ["city"] = "Paris" }),
                }))
            { FinishReason = ChatFinishReason.ToolCalls })
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, "The weather in Paris is 20°C."))
            { FinishReason = ChatFinishReason.Stop });

        // Turn 1: tool call
        var r1 = await mock.Object.GetResponseAsync(
            new List<ChatMessage> { new(ChatRole.User, "weather?") });
        Assert.Equal(ChatFinishReason.ToolCalls, r1.FinishReason);

        // Agent executes tool → FunctionResultContent
        var toolResult = new ChatMessage(ChatRole.Tool,
            [new FunctionResultContent("call_1", "20°C, sunny")]);

        // Turn 2: tool result injected → final text
        var r2 = await mock.Object.GetResponseAsync(new List<ChatMessage>
        {
            new(ChatRole.User, "weather?"),
            r1.Messages[0],
            toolResult,
        });
        Assert.Equal(ChatFinishReason.Stop, r2.FinishReason);
        Assert.Contains("20°C", r2.Text);
    }

    // ── Two sequential tool rounds ──

    [Fact]
    public async Task ToolCall_Moq_SequentialRounds()
    {
        var mock = new Mock<IChatClient>();
        mock.SetupSequence(x => x.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                new List<AIContent>
                {
                    new FunctionCallContent("call_1", "search", new Dictionary<string, object?> { ["q"] = "weather" }),
                }))
            { FinishReason = ChatFinishReason.ToolCalls })
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                new List<AIContent>
                {
                    new FunctionCallContent("call_2", "get_details", new Dictionary<string, object?> { ["id"] = "123" }),
                }))
            { FinishReason = ChatFinishReason.ToolCalls })
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Search complete."))
            { FinishReason = ChatFinishReason.Stop });

        // Round 1
        var r1 = await mock.Object.GetResponseAsync(
            new List<ChatMessage> { new(ChatRole.User, "search") });
        Assert.Equal(ChatFinishReason.ToolCalls, r1.FinishReason);

        // Round 2
        var r2 = await mock.Object.GetResponseAsync(new List<ChatMessage>
        {
            new(ChatRole.User, "search"),
            r1.Messages[0],
            new(ChatRole.Tool, [new FunctionResultContent("call_1", "found 3")]),
        });
        Assert.Equal(ChatFinishReason.ToolCalls, r2.FinishReason);
        Assert.Equal("get_details", r2.Messages[0].Contents.OfType<FunctionCallContent>().First().Name);

        // Round 3
        var r3 = await mock.Object.GetResponseAsync(new List<ChatMessage>
        {
            new(ChatRole.User, "search"),
            r1.Messages[0],
            new(ChatRole.Tool, [new FunctionResultContent("call_1", "found 3")]),
            r2.Messages[0],
            new(ChatRole.Tool, [new FunctionResultContent("call_2", "details here")]),
        });
        Assert.Equal(ChatFinishReason.Stop, r3.FinishReason);
        Assert.Contains("complete", r3.Text);
    }

    // ── Streaming tool calls ──

    [Fact]
    public async Task ToolCall_Moq_Streaming_Single()
    {
        var mock = new Mock<IChatClient>();
        mock.Setup(x => x.GetStreamingResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .Returns(StreamFromToolCalls(("get_weather", new Dictionary<string, object?> { ["city"] = "Paris" })));

        var updates = await mock.Object.GetStreamingResponseAsync(
            new List<ChatMessage> { new(ChatRole.User, "weather?") }).ToListAsync();

        var fc = updates.SelectMany(u => u.Contents).OfType<FunctionCallContent>().ToList();
        Assert.Single(fc);
        Assert.Equal("get_weather", fc[0].Name);
        Assert.Contains(updates, u => u.FinishReason == ChatFinishReason.ToolCalls);
    }

    [Fact]
    public async Task ToolCall_Moq_Streaming_ToolThenText()
    {
        var mock = new Mock<IChatClient>();
        // Turn 1: messages without tool roles → tool call stream
        mock.Setup(x => x.GetStreamingResponseAsync(
                It.Is<IEnumerable<ChatMessage>>(msgs => !msgs.Any(m => m.Role == ChatRole.Tool)),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .Returns(StreamFromToolCalls(("search", new Dictionary<string, object?> { ["q"] = "test" })));
        // Turn 2: messages with tool roles → text stream
        mock.Setup(x => x.GetStreamingResponseAsync(
                It.Is<IEnumerable<ChatMessage>>(msgs => msgs.Any(m => m.Role == ChatRole.Tool)),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .Returns(StreamFrom("Found 3 results."));

        // Turn 1 streaming
        var u1 = await mock.Object.GetStreamingResponseAsync(
            new List<ChatMessage> { new(ChatRole.User, "search") }).ToListAsync();
        Assert.Contains(u1, u => u.Contents.OfType<FunctionCallContent>().Any());
        Assert.Contains(u1, u => u.FinishReason == ChatFinishReason.ToolCalls);

        // Turn 2 streaming
        var u2 = await mock.Object.GetStreamingResponseAsync(new List<ChatMessage>
        {
            new(ChatRole.User, "search"),
            new(ChatRole.Assistant, [new FunctionCallContent("call_0", "search", new Dictionary<string, object?>())]),
            new(ChatRole.Tool, [new FunctionResultContent("call_0", "results")]),
        }).ToListAsync();
        Assert.Contains("3 results", u2[0].Text);
    }

    // ── Explicit CallId ──

    [Fact]
    public async Task ToolCall_Moq_ExplicitCallId()
    {
        var mock = new Mock<IChatClient>();
        mock.Setup(x => x.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                new List<AIContent>
                {
                    new FunctionCallContent("custom_id_42", "search",
                        new Dictionary<string, object?> { ["q"] = "test" }),
                }))
            { FinishReason = ChatFinishReason.ToolCalls });

        var response = await mock.Object.GetResponseAsync(
            new List<ChatMessage> { new(ChatRole.User, "search") });

        var fc = Assert.Single(response.Messages[0].Contents.OfType<FunctionCallContent>());
        Assert.Equal("custom_id_42", fc.CallId);
    }

    // ── Context consistency: results injected after tool call ──

    [Fact]
    public async Task ToolCall_Moq_ContextWithToolResults()
    {
        var mock = new Mock<IChatClient>();
        mock.SetupSequence(x => x.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                new List<AIContent> { new FunctionCallContent("call_1", "first_tool", null!) }))
            { FinishReason = ChatFinishReason.ToolCalls })
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Final answer after tool."))
            { FinishReason = ChatFinishReason.Stop });

        var r1 = await mock.Object.GetResponseAsync(
            new List<ChatMessage> { new(ChatRole.User, "do it") });
        Assert.Equal(ChatFinishReason.ToolCalls, r1.FinishReason);

        // Pass context with FunctionResultContent
        var r2 = await mock.Object.GetResponseAsync(new List<ChatMessage>
        {
            new(ChatRole.User, "do it"),
            r1.Messages[0],
            new(ChatRole.Tool, [new FunctionResultContent("call_1", "tool executed")]),
        });
        Assert.Equal(ChatFinishReason.Stop, r2.FinishReason);
        Assert.Equal("Final answer after tool.", r2.Text);
    }

    // ── Agent Tool Calling Lifecycle (new) ──

    [Fact]
    public async Task AgentToolCalling_Moq_FullLifecycle()
    {
        var mock = new Mock<IChatClient>();
        mock.SetupSequence(x => x.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                new List<AIContent>
                {
                    new FunctionCallContent("call_1", "get_weather",
                        new Dictionary<string, object?> { ["city"] = "Paris" }),
                }))
            { FinishReason = ChatFinishReason.ToolCalls })
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, "The weather in Paris is 20°C."))
            { FinishReason = ChatFinishReason.Stop });

        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "What's the weather in Paris?"),
        };

        var response = await AgentToolCallingHelper.RunAsync(mock.Object, messages,
            fc => Task.FromResult("20°C, sunny"));

        Assert.Equal("The weather in Paris is 20°C.", response.Text);
        Assert.Equal(ChatFinishReason.Stop, response.FinishReason);

        // Verify two LLM calls: initial + after tool result
        mock.Verify(x => x.GetResponseAsync(
            It.IsAny<IEnumerable<ChatMessage>>(),
            It.IsAny<ChatOptions?>(),
            It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task AgentToolCalling_Moq_MultiToolRound()
    {
        var mock = new Mock<IChatClient>();
        mock.SetupSequence(x => x.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                new List<AIContent>
                {
                    new FunctionCallContent("call_1", "search_db", new Dictionary<string, object?> { ["query"] = "users" }),
                    new FunctionCallContent("call_2", "search_db", new Dictionary<string, object?> { ["query"] = "orders" }),
                }))
            { FinishReason = ChatFinishReason.ToolCalls })
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                new List<AIContent>
                {
                    new FunctionCallContent("call_3", "join_results", null!),
                }))
            { FinishReason = ChatFinishReason.ToolCalls })
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Found 42 users and 128 orders."))
            { FinishReason = ChatFinishReason.Stop });

        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "Show me user and order stats."),
        };

        var toolResults = new Dictionary<string, string>
        {
            ["call_1"] = "[{'name':'Alice'},{'name':'Bob'}]",
            ["call_2"] = "[{'id':1,'total':99.0}]",
            ["call_3"] = "merged: 42 users, 128 orders",
        };

        var response = await AgentToolCallingHelper.RunAsync(mock.Object, messages,
            fc => Task.FromResult(toolResults.GetValueOrDefault(fc.CallId, "unknown")));

        Assert.Contains("42 users", response.Text);
        Assert.Contains("128 orders", response.Text);
        Assert.Equal(ChatFinishReason.Stop, response.FinishReason);

        // Three LLM calls (initial + 2 tool rounds)
        mock.Verify(x => x.GetResponseAsync(
            It.IsAny<IEnumerable<ChatMessage>>(),
            It.IsAny<ChatOptions?>(),
            It.IsAny<CancellationToken>()), Times.Exactly(3));
    }

    [Fact]
    public async Task AgentToolCalling_Moq_NoToolCalls_ReturnsDirectly()
    {
        var mock = new Mock<IChatClient>();
        mock.Setup(x => x.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Direct answer."))
            { FinishReason = ChatFinishReason.Stop });

        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "Hello!"),
        };

        var response = await AgentToolCallingHelper.RunAsync(mock.Object, messages,
            fc => Task.FromResult("irrelevant"));

        Assert.Equal("Direct answer.", response.Text);

        // Only 1 call, tool never needed
        mock.Verify(x => x.GetResponseAsync(
            It.IsAny<IEnumerable<ChatMessage>>(),
            It.IsAny<ChatOptions?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AgentToolCalling_Moq_VerifyToolArgumentsPassed()
    {
        FunctionCallContent? capturedCall = null;
        var mock = new Mock<IChatClient>();
        mock.SetupSequence(x => x.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                new List<AIContent>
                {
                    new FunctionCallContent("call_x", "multiply",
                        new Dictionary<string, object?> { ["a"] = 6, ["b"] = 7 }),
                }))
            { FinishReason = ChatFinishReason.ToolCalls })
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, "42"))
            { FinishReason = ChatFinishReason.Stop });

        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "What is 6 times 7?"),
        };

        var response = await AgentToolCallingHelper.RunAsync(mock.Object, messages,
            fc =>
            {
                capturedCall = fc;
                var a = (int)fc.Arguments!["a"]!;
                var b = (int)fc.Arguments!["b"]!;
                return Task.FromResult((a * b).ToString());
            });

        Assert.Equal("42", response.Text);

        Assert.NotNull(capturedCall);
        Assert.Equal("multiply", capturedCall.Name);
        Assert.Equal(6, (int)capturedCall.Arguments!["a"]!);
        Assert.Equal(7, (int)capturedCall.Arguments!["b"]!);

        mock.Verify(x => x.GetResponseAsync(
            It.IsAny<IEnumerable<ChatMessage>>(),
            It.IsAny<ChatOptions?>(),
            It.IsAny<CancellationToken>()), Times.Exactly(2));
    }
}
