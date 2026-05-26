using LTAI.Knowledge.Core;
using LTAI.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Skills;

public sealed class SkillSyncService : BackgroundService
{
    private readonly SkillPublisher _publisher;
    private readonly SkillRegistry _registry;
    private readonly ILogger<SkillSyncService> _logger;
    private readonly string _skillsRoot;

    private static readonly TimeSpan GitPullInterval = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan PeerExchangeInterval = TimeSpan.FromMinutes(5);

    public SkillSyncService(
        SkillPublisher publisher,
        SkillRegistry registry,
        ILogger<SkillSyncService> logger,
        string? skillsRoot = null)
    {
        _publisher = publisher;
        _registry = registry;
        _logger = logger;
        _skillsRoot = skillsRoot ?? OptionService.Get("paths.skills") ?? Path.Combine(AppContext.BaseDirectory, "skills");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("SkillSyncService starting");

        await PullConfiguredReposAsync(stoppingToken).ConfigureAwait(false);

        using var gitTimer = new PeriodicTimer(GitPullInterval);
        using var peerTimer = new PeriodicTimer(PeerExchangeInterval);

        var gitTask = RunGitPullsAsync(gitTimer, stoppingToken);
        var peerTask = RunPeerExchangesAsync(peerTimer, stoppingToken);

        await Task.WhenAll(gitTask, peerTask).ConfigureAwait(false);
    }

    private async Task RunGitPullsAsync(PeriodicTimer timer, CancellationToken ct)
    {
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            try { await PullConfiguredReposAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.LogWarning(ex, "Git pull cycle failed"); }
        }
    }

    private async Task RunPeerExchangesAsync(PeriodicTimer timer, CancellationToken ct)
    {
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            try
            {
                var manifest = await _publisher.GetLocalSkillManifestAsync(ct).ConfigureAwait(false);
                _logger.LogDebug("Peer exchange cycle: {Count} local skills", manifest.Count);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.LogWarning(ex, "Peer exchange cycle failed"); }
        }
    }

    private async Task PullConfiguredReposAsync(CancellationToken ct)
    {
        var reposEnv = OptionService.Get("SKILL_REPOS");
        if (string.IsNullOrWhiteSpace(reposEnv))
        {
            _logger.LogDebug("No SKILL_REPOS configured");
            return;
        }

        var repos = reposEnv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var repo in repos)
        {
            try
            {
                _logger.LogInformation("Pulling skills from repo: {Repo}", repo);
                var (count, _) = await _publisher.PullSkillsFromGitAsync(repo, ct: ct).ConfigureAwait(false);
                _logger.LogInformation("Pulled {Count} skills from {Repo}", count, repo);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to pull from repo {Repo}", repo);
            }
        }
    }
}
