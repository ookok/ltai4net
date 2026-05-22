namespace LTAI.Tools.Tools;

public sealed class GralWrapper
{
    private readonly List<GralParticle> _particles = new();
    private readonly Random _rng = new(42);

    public GralResult RunDispersion(GralInput input)
    {
        _particles.Clear();
        var results = new List<GralReceptorResult>();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        foreach (var receptor in input.Receptors)
        {
            var conc = 0.0;
            var particlesInReceptor = 0;

            for (var i = 0; i < input.ParticleCount; i++)
            {
                var (px, py, pz) = LagrangianStep(input, receptor, i);
                var dist = Math.Sqrt(Math.Pow(px - receptor.X, 2) + Math.Pow(py - receptor.Y, 2));

                if (dist < input.ReceptorRadius)
                {
                    conc += input.EmissionRate / (input.WindSpeed * input.ReceptorRadius * input.ReceptorRadius) * 1e6;
                    particlesInReceptor++;
                    _particles.Add(new GralParticle { X = px, Y = py, Z = pz, ReceptorName = receptor.Name });
                }
            }

            results.Add(new GralReceptorResult(
                receptor.Name, receptor.X, receptor.Y,
                Math.Round(conc, 4), particlesInReceptor));
        }

        return new GralResult
        {
            Results = results,
            ParticleCount = input.ParticleCount,
            DurationMs = sw.ElapsedMilliseconds,
            Method = "Lagrangian particle tracking (GRAL-compatible)"
        };
    }

    private (double x, double y, double z) LagrangianStep(GralInput input, GralReceptor receptor, int seed)
    {
        var rng = new Random(42 + seed);
        var dt = input.TimeStep;
        var u = input.WindSpeed + (rng.NextDouble() - 0.5) * input.WindSigma * 2;
        var v = (rng.NextDouble() - 0.5) * input.WindSigma * 2;
        var w = (rng.NextDouble() - 0.5) * input.VerticalSigma * 2;

        var dispersionTime = receptor.Distance / Math.Max(u, 0.1);
        var steps = (int)(dispersionTime / dt);

        double x = 0, y = 0, z = input.SourceHeight;

        for (var s = 0; s < Math.Min(steps, 1000); s++)
        {
            x += u * dt;
            y += v * dt;
            z += w * dt;

            if (z < 0) { z = -z; w = Math.Abs(w) * 0.5; }
            if (z > input.MixingHeight) { z = input.MixingHeight; w = -Math.Abs(w) * 0.5; }

            u += (rng.NextDouble() - 0.5) * input.WindSigma * 0.1;
            v += (rng.NextDouble() - 0.5) * input.WindSigma * 0.1;
            w += (rng.NextDouble() - 0.5) * input.VerticalSigma * 0.1;
        }

        return (receptor.X + x * 0.1, receptor.Y + y * 0.1, z);
    }
}

public sealed class GralInput
{
    public double EmissionRate { get; set; } = 1.0;
    public double SourceHeight { get; set; } = 50;
    public double WindSpeed { get; set; } = 3.0;
    public double WindSigma { get; set; } = 0.5;
    public double VerticalSigma { get; set; } = 0.3;
    public double MixingHeight { get; set; } = 800;
    public double TimeStep { get; set; } = 0.1;
    public double ReceptorRadius { get; set; } = 50;
    public int ParticleCount { get; set; } = 500;
    public List<GralReceptor> Receptors { get; set; } = new()
    {
        new("origin", 0, 0, 0), new("R100m", 100, 0, 100),
        new("R200m", 200, 0, 200), new("R500m", 500, 0, 500), new("R1km", 1000, 0, 1000)
    };
}

public sealed record GralReceptor(string Name, double X, double Y, double Distance);

public sealed class GralResult
{
    public List<GralReceptorResult> Results { get; set; } = new();
    public int ParticleCount { get; set; }
    public long DurationMs { get; set; }
    public string Method { get; set; } = "";
}

public sealed record GralReceptorResult(string Name, double X, double Y, double Concentration, int Particles);

public sealed record GralParticle
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public string ReceptorName { get; set; } = "";
}
