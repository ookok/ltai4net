using System.Collections.Concurrent;

namespace LTAI.Metrics.Evaluation;

public enum ValidationDimension
{
    Univariate,
    Bivariate,
    Multivariate,
    Sequence,
    Covariate
}

public enum Severity
{
    Critical,
    Warning,
    Caution,
    Pass
}

public sealed record DimensionReport(
    ValidationDimension Dimension,
    double Score,
    double KsStatistic,
    double MeanDiffPct,
    double StdDiffPct,
    double CorrelationDiff,
    List<double> HistogramBins)
{
    public bool Passed => Score >= 75;

    public Severity SeverityLevel => Score switch
    {
        < 40 => Severity.Critical,
        < 60 => Severity.Warning,
        < 75 => Severity.Caution,
        _ => Severity.Pass
    };
}

public sealed record SSDataReport(
    string Id,
    string Target,
    List<DimensionReport> Dimensions,
    List<string> Warnings,
    List<string> Recommendations)
{
    public double OverallScore => Dimensions.Count == 0 ? 0 : Dimensions.Average(d => d.Score);
    public bool Passed => Dimensions.All(d => d.Passed);
    public int CriticalCount => Dimensions.Count(d => d.SeverityLevel == Severity.Critical);
}

public sealed class StatisticalRealismValidator
{
    public static readonly Lazy<StatisticalRealismValidator> Instance = new(() => new StatisticalRealismValidator());

    private readonly ConcurrentDictionary<string, SSDataReport> _reports = new();

    public DimensionReport ValidateUnivariate(List<double> synthetic, List<double> reference, string dimName, int bins = 20)
    {
        var synthArr = synthetic.ToArray();
        var refArr = reference.ToArray();

        double ksStatistic = TwoSampleKS(synthArr, refArr);

        double avgS = synthArr.Length > 0 ? synthArr.Average() : 0;
        double avgR = refArr.Length > 0 ? refArr.Average() : 0;
        double meanDiffPct = Math.Abs(avgS - avgR) / Math.Max(Math.Abs(avgR), 0.001) * 100;

        double stdS = synthArr.Length > 0 ? StdDev(synthArr, avgS) : 0;
        double stdR = refArr.Length > 0 ? StdDev(refArr, avgR) : 0;
        double stdDiffPct = stdR > 0.001 ? Math.Abs(stdS - stdR) / stdR * 100 : 0;

        double ksScore = (1 - ksStatistic) * 100;
        double meanScore = Math.Max(0, 100 - meanDiffPct);
        double stdScore = Math.Max(0, 100 - stdDiffPct * 2);

        double score = ksScore * 0.5 + meanScore * 0.25 + stdScore * 0.25;

        var histogramBins = ComputeHistogram(synthArr, refArr, bins);

        return new DimensionReport(
            ValidationDimension.Univariate,
            score,
            ksStatistic,
            meanDiffPct,
            stdDiffPct,
            0,
            histogramBins);
    }

    public DimensionReport ValidateBivariate(List<double> synthX, List<double> synthY, List<double> refX, List<double> refY, string dimName)
    {
        var sx = synthX.ToArray();
        var sy = synthY.ToArray();
        var rx = refX.ToArray();
        var ry = refY.ToArray();

        double corrS = PearsonCorrelation(sx, sy);
        double corrR = PearsonCorrelation(rx, ry);
        double correlationDiff = Math.Abs(corrS - corrR);

        var (slope, intercept) = LinearFit(rx, ry);

        var synthResiduals = new double[sx.Length];
        for (int i = 0; i < sx.Length; i++)
            synthResiduals[i] = sy[i] - (slope * sx[i] + intercept);

        var refResiduals = new double[rx.Length];
        for (int i = 0; i < rx.Length; i++)
            refResiduals[i] = ry[i] - (slope * rx[i] + intercept);

        double ksStatistic = TwoSampleKS(synthResiduals, refResiduals);

        double score = Math.Max(0, (1 - correlationDiff) * 0.6 * 100 + (1 - ksStatistic) * 0.4 * 100);

        return new DimensionReport(
            ValidationDimension.Bivariate,
            score,
            ksStatistic,
            0,
            0,
            correlationDiff,
            new List<double>());
    }

    public DimensionReport ValidateMultivariate(List<List<double>> synthFeatures, List<double> synthOutcomes, List<List<double>> refFeatures, List<double> refOutcomes, string dimName)
    {
        int featureCount = synthFeatures.Count;
        double avgMeanDiffPct = 0;
        double avgStdDiffPct = 0;

        var ksStats = new List<double>();

        for (int i = 0; i < featureCount; i++)
        {
            var sf = synthFeatures[i].ToArray();
            var rf = refFeatures[i].ToArray();

            double ks = TwoSampleKS(sf, rf);
            ksStats.Add(ks);

            double avgS = sf.Length > 0 ? sf.Average() : 0;
            double avgR = rf.Length > 0 ? rf.Average() : 0;
            avgMeanDiffPct += Math.Abs(avgS - avgR) / Math.Max(Math.Abs(avgR), 0.001) * 100;

            double stdS = sf.Length > 0 ? StdDev(sf, avgS) : 0;
            double stdR = rf.Length > 0 ? StdDev(rf, avgR) : 0;
            avgStdDiffPct += stdR > 0.001 ? Math.Abs(stdS - stdR) / stdR * 100 : 0;
        }

        double avgKs = ksStats.Average();
        double score = Math.Max(0, (1 - avgKs) * 100);

        return new DimensionReport(
            ValidationDimension.Multivariate,
            score,
            avgKs,
            avgMeanDiffPct / Math.Max(featureCount, 1),
            avgStdDiffPct / Math.Max(featureCount, 1),
            0,
            new List<double>());
    }

    public DimensionReport ValidateSequence(List<List<string>> synthSeqs, List<List<string>> refSeqs, string dimName)
    {
        var synthBigrams = synthSeqs.SelectMany(ExtractBigrams).ToList();
        var refBigrams = refSeqs.SelectMany(ExtractBigrams).ToList();

        var synthTop20 = synthBigrams
            .GroupBy(b => b)
            .OrderByDescending(g => g.Count())
            .Take(20)
            .Select(g => g.Key)
            .ToHashSet();

        var refTop20 = refBigrams
            .GroupBy(b => b)
            .OrderByDescending(g => g.Count())
            .Take(20)
            .Select(g => g.Key)
            .ToHashSet();

        double jaccard = JaccardSet(synthTop20, refTop20);
        double score = Math.Max(0, jaccard * 100);

        return new DimensionReport(
            ValidationDimension.Sequence,
            score,
            1 - jaccard,
            0,
            0,
            0,
            new List<double>());
    }

    public DimensionReport ValidateCovariate(List<List<string>> synthSeqs, List<double> synthCovar, List<List<string>> refSeqs, List<double> refCovar)
    {
        var synthLengths = synthSeqs.Select(s => (double)s.Count).ToArray();
        var refLengths = refSeqs.Select(s => (double)s.Count).ToArray();

        double corrS = PearsonCorrelation(synthLengths, synthCovar.ToArray());
        double corrR = PearsonCorrelation(refLengths, refCovar.ToArray());
        double correlationDiff = Math.Abs(corrS - corrR);

        double score = Math.Max(0, (1 - correlationDiff) * 100);

        return new DimensionReport(
            ValidationDimension.Covariate,
            score,
            0,
            0,
            0,
            correlationDiff,
            new List<double>());
    }

    public SSDataReport CreateReport(string target, List<DimensionReport> dims)
    {
        var reportId = Guid.NewGuid().ToString("N");
        var warnings = new List<string>();
        var recommendations = new List<string>();

        foreach (var dim in dims)
        {
            switch (dim.SeverityLevel)
            {
                case Severity.Critical:
                    warnings.Add($"Critical [{dim.Dimension}]: score={dim.Score:F1}");
                    recommendations.Add(GetRecommendation(dim));
                    break;
                case Severity.Warning:
                    warnings.Add($"Warning [{dim.Dimension}]: score={dim.Score:F1}");
                    recommendations.Add(GetRecommendation(dim));
                    break;
            }

            if (dim.Dimension == ValidationDimension.Univariate && dim.StdDiffPct > 90)
            {
                warnings.Add($"Variance collapse detected in {dim.Dimension}: StdDiffPct={dim.StdDiffPct:F1}%");
            }
        }

        var report = new SSDataReport(reportId, target, dims, warnings, recommendations);
        _reports[reportId] = report;
        return report;
    }

    public SSDataReport? LoadReport(string reportId)
    {
        _reports.TryGetValue(reportId, out var report);
        return report;
    }

    public List<SSDataReport> ListReports(string? target = null)
    {
        var reports = _reports.Values.AsEnumerable();
        if (target is not null)
            reports = reports.Where(r => r.Target == target);
        return reports.ToList();
    }

    public static double TwoSampleKS(double[] a, double[] b)
    {
        if (a.Length == 0 && b.Length == 0) return 0;
        if (a.Length == 0 || b.Length == 0) return 1.0;

        int n = a.Length;
        int m = b.Length;

        var combined = new (double val, bool isA)[n + m];
        for (int i = 0; i < n; i++) combined[i] = (a[i], true);
        for (int i = 0; i < m; i++) combined[n + i] = (b[i], false);

        Array.Sort(combined, (x, y) => x.val.CompareTo(y.val));

        double countA = 0, countB = 0;
        double maxDiff = 0;

        for (int i = 0; i < combined.Length; i++)
        {
            if (combined[i].isA) countA++; else countB++;

            double cdfA = countA / n;
            double cdfB = countB / m;
            double diff = Math.Abs(cdfA - cdfB);
            if (diff > maxDiff) maxDiff = diff;
        }

        return maxDiff;
    }

    public static double PearsonCorrelation(double[] x, double[] y)
    {
        if (x.Length == 0 || y.Length == 0) return 0;

        int n = Math.Min(x.Length, y.Length);
        if (n < 2) return 0;

        double meanX = 0, meanY = 0;
        for (int i = 0; i < n; i++)
        {
            meanX += x[i];
            meanY += y[i];
        }
        meanX /= n;
        meanY /= n;

        double cov = 0, varX = 0, varY = 0;
        for (int i = 0; i < n; i++)
        {
            double dx = x[i] - meanX;
            double dy = y[i] - meanY;
            cov += dx * dy;
            varX += dx * dx;
            varY += dy * dy;
        }

        double denom = Math.Sqrt(varX * varY);
        return denom < 1e-15 ? 0 : cov / denom;
    }

    public static List<string> ExtractBigrams(List<string> sequence)
    {
        var bigrams = new List<string>();
        for (int i = 0; i < sequence.Count - 1; i++)
        {
            bigrams.Add($"{sequence[i]}|{sequence[i + 1]}");
        }
        return bigrams;
    }

    public static double JaccardSet(HashSet<string> a, HashSet<string> b)
    {
        var intersection = new HashSet<string>(a);
        intersection.IntersectWith(b);
        var union = new HashSet<string>(a);
        union.UnionWith(b);
        return union.Count == 0 ? 0 : (double)intersection.Count / union.Count;
    }

    private static double StdDev(double[] values, double mean)
    {
        if (values.Length == 0) return 0;
        double sumSq = 0;
        for (int i = 0; i < values.Length; i++)
        {
            double diff = values[i] - mean;
            sumSq += diff * diff;
        }
        return Math.Sqrt(sumSq / values.Length);
    }

    private static (double slope, double intercept) LinearFit(double[] x, double[] y)
    {
        int n = Math.Min(x.Length, y.Length);
        if (n < 2) return (0, x.Length > 0 && y.Length > 0 ? y[0] : 0);

        double meanX = 0, meanY = 0;
        for (int i = 0; i < n; i++)
        {
            meanX += x[i];
            meanY += y[i];
        }
        meanX /= n;
        meanY /= n;

        double cov = 0, varX = 0;
        for (int i = 0; i < n; i++)
        {
            double dx = x[i] - meanX;
            cov += dx * (y[i] - meanY);
            varX += dx * dx;
        }

        double slope = varX < 1e-15 ? 0 : cov / varX;
        double intercept = meanY - slope * meanX;
        return (slope, intercept);
    }

    private static List<double> ComputeHistogram(double[] synthetic, double[] reference, int bins)
    {
        var result = new List<double>();
        if (synthetic.Length == 0 && reference.Length == 0) return result;

        double minVal = Math.Min(
            synthetic.Length > 0 ? synthetic.Min() : reference.Min(),
            reference.Length > 0 ? reference.Min() : synthetic.Min());
        double maxVal = Math.Max(
            synthetic.Length > 0 ? synthetic.Max() : reference.Max(),
            reference.Length > 0 ? reference.Max() : synthetic.Max());

        double range = maxVal - minVal;
        if (range < 1e-15)
        {
            for (int i = 0; i < bins; i++)
                result.Add(i == bins / 2 ? synthetic.Length : 0);
            return result;
        }

        double binWidth = range / bins;

        for (int i = 0; i < bins; i++)
        {
            double binLow = minVal + i * binWidth;
            double binHigh = i == bins - 1 ? maxVal + 1e-10 : binLow + binWidth;

            int count = 0;
            foreach (var v in synthetic)
            {
                if (v >= binLow && v < binHigh)
                    count++;
            }
            result.Add(count);
        }

        return result;
    }

    private static string GetRecommendation(DimensionReport dim)
    {
        return dim.Dimension switch
        {
            ValidationDimension.Univariate => dim.MeanDiffPct > 20
                ? $"Univariate mean mismatch high ({dim.MeanDiffPct:F1}%); check distribution center"
                : dim.KsStatistic > 0.3
                    ? $"Univariate KS distance is {dim.KsStatistic:F2}; adjust distribution shape"
                    : $"Univariate score is {dim.Score:F0}/100; review synthetic data generation parameters",
            ValidationDimension.Bivariate => $"Bivariate correlation diff is {dim.CorrelationDiff:F3}; improve dependency modeling",
            ValidationDimension.Multivariate => $"Multivariate KS average is {dim.KsStatistic:F3}; improve feature joint distribution",
            ValidationDimension.Sequence => $"Sequence bigram overlap is {(1 - dim.KsStatistic) * 100:F0}%; improve transition modeling",
            ValidationDimension.Covariate => $"Covariate correlation diff is {dim.CorrelationDiff:F3}; review covariate transformations",
            _ => "Review synthetic data generation pipeline"
        };
    }
}
