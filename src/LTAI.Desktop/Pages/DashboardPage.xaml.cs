using LTAI.DNA;

namespace LTAI.Desktop.Pages;

public partial class DashboardPage : ContentPage
{
    private readonly LTAIService _svc;
    private IDispatcherTimer? _timer;

    public DashboardPage(LTAIService svc)
    {
        InitializeComponent();
        _svc = svc;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _timer = Dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(3);
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Refresh();
    }

    protected override void OnDisappearing()
    {
        _timer?.Stop();
        base.OnDisappearing();
    }

    private void Refresh()
    {
        var dna = _svc.DNA;
        if (dna != null)
        {
            ConsciousnessLabel.Text = $"Consciousness: {dna.Consciousness.State.Level} ({dna.Consciousness.State.AwarenessScore:F2})";
            EvolutionLabel.Text = $"Evolution: {dna.Evolution.Phase} Gen{dna.Evolution.CurrentGenome.Generation} Fit:{dna.Evolution.CurrentGenome.FitnessScore:F3}";
            SafetyLabel.Text = $"Safety: {dna.Safety.Posture}";
            BiorhythmLabel.Text = $"Biorhythm: {dna.Life.Biorhythm.Phase} E:{dna.Life.Biorhythm.EnergyLevel:F1}";

            var h = dna.Life.Hormones;
            DopamineLabel.Text = $"Dopamine {h.Dopamine:F2}";
            SerotoninLabel.Text = $"Serotonin {h.Serotonin:F2}";
            CortisolLabel.Text = $"Cortisol {h.Cortisol:F2}";

            var p = dna.Life.Personality;
            OLabel.Text = $"O:{p.Openness:F2}"; OBar.Progress = p.Openness;
            CLabel.Text = $"C:{p.Conscientiousness:F2}"; CBar.Progress = p.Conscientiousness;
            ELabel.Text = $"E:{p.Extraversion:F2}"; EBar.Progress = p.Extraversion;
            ALabel.Text = $"A:{p.Agreeableness:F2}"; ABar.Progress = p.Agreeableness;
            NLabel.Text = $"N:{p.Neuroticism:F2}"; NBar.Progress = p.Neuroticism;

            GenesStack.Children.Clear();
            foreach (var (name, gene) in dna.Evolution.CurrentGenome.Genes.Take(8))
            {
                var g = new Grid { ColumnDefinitions = { new ColumnDefinition(120), new ColumnDefinition(GridLength.Star), new ColumnDefinition(50) } };
                g.Add(new Label { Text = name, FontSize = 12, TextColor = Color.FromArgb("#8b949e") }, 0);
                var pb = new ProgressBar { Progress = gene.Expression, ProgressColor = Color.FromArgb("#3fb950") };
                g.Add(pb, 1);
                g.Add(new Label { Text = gene.Expression.ToString("F2"), FontSize = 12, TextColor = Color.FromArgb("#c9d1d9"), HorizontalTextAlignment = TextAlignment.End }, 2);
                GenesStack.Children.Add(g);
            }
        }

        ModeLabel.Text = $"Mode: {_svc.LTS.Mode}";

        var metrics = _svc.Metrics;
        if (metrics != null)
        {
            var s = metrics.GetSnapshot();
            RequestsLabel.Text = $"Requests: {s.TotalRequests}";
            TokensLabel.Text = $"Tokens: {s.TotalTokens}";
            LatencyLabel.Text = $"Latency: {s.AvgLatencyMs:F0}ms";
            MemoryLabel.Text = $"MEM: {s.MemoryMb}MB";
        }
    }
}
