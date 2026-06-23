using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Xunit;

namespace LTAI.Tests;

/// <summary>Starts an Ollama container via Testcontainers for integration tests.</summary>
public sealed class OllamaFixture : IAsyncLifetime
{
    private readonly IContainer _container;

    public string BaseUrl => $"http://localhost:{_container.GetMappedPublicPort(11434)}";

    public OllamaFixture()
    {
        _container = new ContainerBuilder()
            .WithImage("ollama/ollama:latest")
            .WithPortBinding(11434, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilPortIsAvailable(11434))
            .Build();
    }

    public async Task InitializeAsync()
    {
        try
        {
            await _container.StartAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new SkipException($"Ollama container failed to start: {ex.Message}. Is Docker installed?");
        }
    }

    public async Task DisposeAsync()
    {
        try { await _container.DisposeAsync().ConfigureAwait(false); }
        catch { /* best-effort cleanup */ }
    }
}

[CollectionDefinition("Ollama")]
public sealed class OllamaCollection : ICollectionFixture<OllamaFixture> { }

/// <summary>Integration tests against a real Ollama instance via Testcontainers.</summary>
[Collection("Ollama")]
public sealed class OllamaIntegrationTests
{
    private readonly OllamaFixture _fixture;
    private readonly HttpClient _http;

    public OllamaIntegrationTests(OllamaFixture fixture)
    {
        _fixture = fixture;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    [SkippableFact]
    public async Task Health_Endpoint_ReturnsOk()
    {
        var resp = await _http.GetAsync($"{_fixture.BaseUrl}").ConfigureAwait(false);
        Assert.True(resp.IsSuccessStatusCode);
    }

    [SkippableFact]
    public async Task ListModels_ReturnsAtLeastDefault()
    {
        var resp = await _http.GetAsync($"{_fixture.BaseUrl}/api/tags").ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        if (!await HasModelAsync("llama3.2:1b").ConfigureAwait(false))
        {
            var pullResp = await _http.PostAsync(
                $"{_fixture.BaseUrl}/api/pull",
                new StringContent(JsonSerializer.Serialize(new { name = "llama3.2:1b" }),
                    System.Text.Encoding.UTF8, "application/json")).ConfigureAwait(false);
            pullResp.EnsureSuccessStatusCode();
        }

        var tagsResp = await _http.GetAsync($"{_fixture.BaseUrl}/api/tags").ConfigureAwait(false);
        var body = await tagsResp.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.Contains("llama3.2", body, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task Generate_SimplePrompt_ReturnsResponse()
    {
        if (!await HasModelAsync("llama3.2:1b").ConfigureAwait(false))
            return;

        var payload = JsonSerializer.Serialize(new
        {
            model = "llama3.2:1b",
            prompt = "Hello, respond with just the word OK.",
            stream = false
        });

        var resp = await _http.PostAsync(
            $"{_fixture.BaseUrl}/api/generate",
            new StringContent(payload, System.Text.Encoding.UTF8, "application/json")
        ).ConfigureAwait(false);

        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.Contains("response", body, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<bool> HasModelAsync(string model)
    {
        try
        {
            var resp = await _http.GetAsync($"{_fixture.BaseUrl}/api/tags").ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return false;
            var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            return body.Contains(model, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}
