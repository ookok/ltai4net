using Xunit;
using LTAI.Agent;

namespace LTAI.Tests;

public sealed class AgentDefinitionTests
{
    private static IReadOnlyList<AgentFileDef> AllAgents => AgentRegistry.LoadAll();

    [Fact]
    public void LoadAll_ReturnsAllAgents()
    {
        var defs = AllAgents;
        Assert.Equal(21, defs.Count);
    }

    [Theory]
    [InlineData("LTAI-Chat")]
    [InlineData("LTAI-Chat-Pro")]
    [InlineData("LTAI-Code")]
    [InlineData("LTAI-Data")]
    [InlineData("LTAI-Frontend")]
    [InlineData("LTAI-LLM")]
    [InlineData("LTAI-Math")]
    [InlineData("LTAI-System")]
    [InlineData("LTAI-Writer")]
    [InlineData("LTAI-SQL")]
    [InlineData("LTAI-API")]
    [InlineData("LTAI-Arch")]
    [InlineData("LTAI-DCI")]
    [InlineData("LTAI-Test")]
    [InlineData("LTAI-Review")]
    [InlineData("LTAI-Debug")]
    [InlineData("LTAI-Security")]
    [InlineData("LTAI-DevOps")]
    [InlineData("LTAI-Office")]
    [InlineData("LTAI-ScrumMaster")]
    public void EachAgent_ExistsAndHasRequiredFields(string agentName)
    {
        var def = AllAgents.FirstOrDefault(d => d.Name == agentName);
        Assert.NotNull(def);
        Assert.NotNull(def.Name);
        Assert.NotNull(def.Description);
        Assert.False(string.IsNullOrWhiteSpace(def.Prompt),
            $"{agentName}: Prompt should not be empty");
    }

    [Fact]
    public void AllAgents_HaveValidTemperature()
    {
        foreach (var def in AllAgents)
        {
            Assert.True(def.Temperature >= 0.0 && def.Temperature <= 2.0,
                $"{def.Name}: Temperature {def.Temperature} out of range [0, 2]");
        }
    }

    [Fact]
    public void AllAgents_HaveTopP()
    {
        foreach (var def in AllAgents)
            Assert.True(def.TopP >= 0.0 && def.TopP <= 1.0,
                $"{def.Name}: TopP {def.TopP} out of range [0, 1]");
    }

    [Fact]
    public void ChatAgent_HasModelIdL1()
    {
        var chat = AllAgents.First(d => d.Name == "LTAI-Chat");
        Assert.Equal("l1", chat.ModelId);
    }

    [Fact]
    public void ChatProAgent_HasModelIdL2()
    {
        var pro = AllAgents.First(d => d.Name == "LTAI-Chat-Pro");
        Assert.Equal("l2", pro.ModelId);
    }

    [Fact]
    public void LlmAgent_HasNoTools()
    {
        var llm = AllAgents.First(d => d.Name == "LTAI-LLM");
        Assert.Empty(llm.Tools);
        Assert.Empty(llm.Permissions);
    }

    [Fact]
    public void AllAgents_HaveUniqueNames()
    {
        var names = AllAgents.Select(d => d.Name).ToList();
        Assert.Equal(names.Distinct().Count(), names.Count);
    }

    [Fact]
    public void AllAgents_NonEmptyCapabilityText()
    {
        foreach (var def in AllAgents)
            Assert.False(string.IsNullOrWhiteSpace(def.CapabilityText),
                $"{def.Name}: CapabilityText should not be empty");
    }
}
