namespace LTAI.Agent.Memory;

public sealed class MadDenoiser
{
    public IReadOnlyList<(PalaceStore.Drawer Drawer, double Score)> Denoise(
        IReadOnlyList<(PalaceStore.Drawer Drawer, double Score)> candidates,
        double kappa = 2.5)
    {
        if (candidates.Count < 3) return candidates;

        var scores = candidates.Select(c => c.Score).OrderBy(s => s).ToList();
        var median = scores[scores.Count / 2];
        var deviations = scores.Select(s => Math.Abs(s - median)).OrderBy(d => d).ToList();
        var mad = 1.4826 * deviations[deviations.Count / 2];
        if (mad < 1e-10) return candidates;

        var threshold = median - kappa * mad;
        return candidates.Where(c => c.Score >= threshold).ToList();
    }

    public IReadOnlyList<(T Item, double Score)> Denoise<T>(
        IReadOnlyList<(T Item, double Score)> candidates,
        double kappa = 2.5)
    {
        if (candidates.Count < 3) return candidates;

        var scores = candidates.Select(c => c.Score).OrderBy(s => s).ToList();
        var median = scores[scores.Count / 2];
        var deviations = scores.Select(s => Math.Abs(s - median)).OrderBy(d => d).ToList();
        var mad = 1.4826 * deviations[deviations.Count / 2];
        if (mad < 1e-10) return candidates;

        var threshold = median - kappa * mad;
        return candidates.Where(c => c.Score >= threshold).ToList();
    }
}
