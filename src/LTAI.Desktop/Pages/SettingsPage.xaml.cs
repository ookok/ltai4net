namespace LTAI.Desktop.Pages;

public partial class SettingsPage : ContentPage
{
    private readonly LTAIService _svc;

    public SettingsPage(LTAIService svc)
    {
        InitializeComponent();
        _svc = svc;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        var lts = _svc.LTS;
        var dna = _svc.DNA;
        var p = System.Diagnostics.Process.GetCurrentProcess();

        VersionLabel.Text = $"LTAI v7.0.0 — Sentient Mesh";
        DnaLabel.Text = dna != null
            ? $"DNA: {dna.Consciousness.State.Level} | Gen {dna.GetStatus().Generation} | Fitness {dna.GetStatus().FitnessScore:F2} | Safety {dna.Safety.Posture}"
            : "DNA: Offline";
        SafetyLabel.Text = $"v7.0 UnifiedSafetyGate active — all traffic through single gatekeeper";
        RouterLabel.Text = $"v7.0 UnifiedSemanticRouter — embedding + keyword fallback, confidence circuit breaker 0.4";
        SystemLabel.Text = $"System: Mode {lts.Mode} | PID {p.Id} | Threads {p.Threads.Count} | MEM {p.WorkingSet64 / 1024 / 1024}MB | {p.StartTime.ToLocalTime():yyyy-MM-dd HH:mm}";
        ProviderLabel.Text = $"Providers: {lts.Mode} | .NET {Environment.Version}";
    }
}
