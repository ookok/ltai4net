using LTAI.Agent.Snippets;
using LTAI.Core.Commands;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Desktop.Tests;

public sealed class ChatViewComponentTests
{
    [Fact]
    public void RenderMessage_WithCodeBlock_ReturnsFormatted()
    {
        var result = ChatMessageRenderer.SplitCodeBlocks(
            "text\n```csharp\nvar x = 1;\n```\nend");

        Assert.Equal(3, result.Count);
        Assert.Equal("text", result[0].Content.Trim());
        Assert.True(result[0].IsCode == false);
        Assert.Equal("var x = 1;", result[1].Content.Trim());
        Assert.True(result[1].IsCode);
        Assert.Equal("end", result[2].Content.Trim());
        Assert.True(result[2].IsCode == false);
    }

    [Fact]
    public void ExecuteCommand_SlashHelp_ReturnsHelp()
    {
        var parser = new CommandParser();
        var cmd = parser.Parse("/help");

        Assert.IsType<HelpCommand>(cmd);
    }

    [Fact]
    public async Task SnippetManager_AddSnippet_PersistsToList()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ltai-snippet-ut-{Guid.NewGuid():N}.json");
        try
        {
            var store = new SnippetStore(path, NullLogger<SnippetStore>.Instance);
            var snippet = new Snippet
            {
                Key = "test-key",
                Content = "test content",
                Description = "test description"
            };

            await store.SaveAsync(snippet);
            var list = await store.ListAsync();

            Assert.Contains(list, s => s.Key == "test-key");

            var loaded = await store.GetAsync("test-key");
            Assert.NotNull(loaded);
            Assert.Equal("test content", loaded.Content);
            Assert.Equal("test description", loaded.Description);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
