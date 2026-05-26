using System.Globalization;
using System.Text;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace LTAI.Desktop;

public sealed class EiaWorkbenchView : UserControl
{
    private readonly LTAIService _svc;

    private readonly TextBox _stackHeightBox;
    private readonly TextBox _emissionRateBox;
    private readonly TextBox _windSpeedBox;
    private readonly ComboBox _stabilityBox;

    private readonly TextBox _flowRateBox;
    private readonly TextBox _bodBox;
    private readonly TextBox _doBox;
    private readonly TextBox _decayBox;

    private readonly TextBox _noiseSourceBox;
    private readonly TextBox _noiseDistanceBox;
    private readonly TextBox _barrierHeightBox;

    private readonly ComboBox _standardCombo;
    private readonly TextBlock _standardLimits;

    private readonly TextBox _reportBox;
    private readonly TextBlock _resultSummary;

    private readonly TextBox _gpYBox;
    private readonly TextBox _gpDistBox;

    private readonly TextBox _co2Co2Box;
    private readonly TextBox _co2Ch4Box;
    private readonly TextBox _co2N2oBox;
    private readonly TextBlock _co2Result;

    private readonly TextBox _hqExposureBox;
    private readonly TextBox _hqRfdBox;
    private readonly TextBlock _hqResult;

    private readonly TextBox _spDistBox;
    private readonly TextBox _spVelocityBox;
    private readonly TextBlock _spResult;

    private static readonly Dictionary<string, string> StandardData = new()
    {
        ["GB 3095 (Air)"] = "SO2: 60 μg/m³ (annual), 150 μg/m³ (24h)\n" +
                            "NO2: 40 μg/m³ (annual), 80 μg/m³ (24h)\n" +
                            "PM10: 70 μg/m³ (annual), 150 μg/m³ (24h)\n" +
                            "PM2.5: 35 μg/m³ (annual), 75 μg/m³ (24h)\n" +
                            "CO: 4 mg/m³ (24h), O3: 160 μg/m³ (8h)",

        ["GB 3838 (Water)"] = "Class III Surface Water:\n" +
                              "DO ≥ 5 mg/L, BOD5 ≤ 4 mg/L\n" +
                              "COD ≤ 20 mg/L, NH3-N ≤ 1.0 mg/L\n" +
                              "TP ≤ 0.2 mg/L, TN ≤ 1.0 mg/L",

        ["GB 3096 (Noise)"] = "Class 0: 50/40 dB(A) day/night\n" +
                              "Class 1: 55/45 dB(A) residential\n" +
                              "Class 2: 60/50 dB(A) mixed\n" +
                              "Class 3: 65/55 dB(A) industrial\n" +
                              "Class 4: 70/55 dB(A) roadside",
    };

    public EiaWorkbenchView(LTAIService svc)
    {
        _svc = svc;
        Background = LtaiTheme.Sbb(LtaiTheme.Bg);

        _stackHeightBox = new TextBox();
        _emissionRateBox = new TextBox();
        _windSpeedBox = new TextBox();
        _stabilityBox = new ComboBox();
        _flowRateBox = new TextBox();
        _bodBox = new TextBox();
        _doBox = new TextBox();
        _decayBox = new TextBox();
        _noiseSourceBox = new TextBox();
        _noiseDistanceBox = new TextBox();
        _barrierHeightBox = new TextBox();
        _standardCombo = new ComboBox();
        _standardLimits = new TextBlock();
        _reportBox = new TextBox();
        _resultSummary = new TextBlock();
        _gpYBox = new TextBox();
        _gpDistBox = new TextBox();
        _co2Co2Box = new TextBox();
        _co2Ch4Box = new TextBox();
        _co2N2oBox = new TextBox();
        _co2Result = new TextBlock();
        _hqExposureBox = new TextBox();
        _hqRfdBox = new TextBox();
        _hqResult = new TextBlock();
        _spDistBox = new TextBox();
        _spVelocityBox = new TextBox();
        _spResult = new TextBlock();

        var outerGrid = new Grid
        {
            ColumnDefinitions = new("300,*"),
            Margin = new(12)
        };

        var leftPanel = BuildLeftPanel();
        var rightPanel = BuildRightPanel();

        Grid.SetColumn(leftPanel, 0);
        Grid.SetColumn(rightPanel, 1);

        outerGrid.Children.Add(leftPanel);
        outerGrid.Children.Add(rightPanel);

        Content = new ScrollViewer { Content = outerGrid };
    }

    private ScrollViewer BuildLeftPanel()
    {
        var stack = new StackPanel { Spacing = 6 };

        stack.Children.Add(new TextBlock
        {
            Text = "Model Parameters",
            FontSize = 16,
            FontWeight = FontWeight.Bold,
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary)
        });
        stack.Children.Add(new Border { Height = 1, Background = LtaiTheme.Sbb(LtaiTheme.Border) });

        stack.Children.Add(SectionHeader("Atmospheric Dispersion"));
        SetupTextBox(_stackHeightBox, "50");
        AddParamRow(stack, "Stack Height", "m", _stackHeightBox);
        SetupTextBox(_emissionRateBox, "100");
        AddParamRow(stack, "Emission Rate", "g/s", _emissionRateBox);
        SetupTextBox(_windSpeedBox, "3");
        AddParamRow(stack, "Wind Speed", "m/s", _windSpeedBox);

        var stabLabel = new TextBlock
        {
            Text = "Stability Class",
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextSecondary),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Width = 110
        };
        SetupStabilityCombo();
        var stabRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        stabRow.Children.Add(stabLabel);
        stabRow.Children.Add(_stabilityBox);
        stack.Children.Add(stabRow);

        stack.Children.Add(SectionHeader("Water Quality"));
        SetupTextBox(_flowRateBox, "2");
        AddParamRow(stack, "Flow Rate", "m³/s", _flowRateBox);
        SetupTextBox(_bodBox, "20");
        AddParamRow(stack, "BOD", "mg/L", _bodBox);
        SetupTextBox(_doBox, "8");
        AddParamRow(stack, "DO (initial)", "mg/L", _doBox);
        SetupTextBox(_decayBox, "0.3");
        AddParamRow(stack, "Decay Coeff k", "1/d", _decayBox);

        stack.Children.Add(SectionHeader("Noise"));
        SetupTextBox(_noiseSourceBox, "85");
        AddParamRow(stack, "Source Level", "dB(A)", _noiseSourceBox);
        SetupTextBox(_noiseDistanceBox, "50");
        AddParamRow(stack, "Distance", "m", _noiseDistanceBox);
        SetupTextBox(_barrierHeightBox, "3");
        AddParamRow(stack, "Barrier Height", "m", _barrierHeightBox);

        var runBtn = new Button
        {
            Content = "Run Model",
            Background = LtaiTheme.Sbb(LtaiTheme.AccentDNA),
            Foreground = LtaiTheme.Sbb("#ffffff"),
            FontSize = 13,
            Height = 32,
            CornerRadius = new(4),
            Margin = new(0, 8, 0, 0)
        };
        runBtn.Click += (_, _) => RunModel();
        stack.Children.Add(runBtn);

        _resultSummary.FontSize = 11;
        _resultSummary.FontFamily = new("Consolas");
        _resultSummary.Foreground = LtaiTheme.Sbb(LtaiTheme.AccentSystem);
        _resultSummary.TextWrapping = TextWrapping.Wrap;
        _resultSummary.Margin = new(0, 4, 0, 0);
        stack.Children.Add(_resultSummary);

        stack.Children.Add(SectionHeader("Quick Tools"));

        var gpHeader = new TextBlock
        {
            Text = "Gaussian Plume",
            FontSize = 12,
            FontWeight = FontWeight.Bold,
            Foreground = LtaiTheme.Sbb(LtaiTheme.AccentInfo),
            Margin = new(0, 4, 0, 0)
        };
        stack.Children.Add(gpHeader);

        SetupToolTextBox(_gpYBox, "0");
        SetupToolTextBox(_gpDistBox, "500");
        stack.Children.Add(LabeledToolRow("Y offset (m)", _gpYBox));
        stack.Children.Add(LabeledToolRow("Distance (m)", _gpDistBox));

        var gpBtn = SmallToolButton("Calculate Plume");
        gpBtn.Click += (_, _) => CalcGaussianPlume();
        stack.Children.Add(gpBtn);

        var co2Header = new TextBlock
        {
            Text = "CO2 Equivalent",
            FontSize = 12,
            FontWeight = FontWeight.Bold,
            Foreground = LtaiTheme.Sbb(LtaiTheme.AccentInfo),
            Margin = new(0, 6, 0, 0)
        };
        stack.Children.Add(co2Header);

        SetupToolTextBox(_co2Co2Box, "0");
        SetupToolTextBox(_co2Ch4Box, "0");
        SetupToolTextBox(_co2N2oBox, "0");
        stack.Children.Add(LabeledToolRow("CO2 (t)", _co2Co2Box));
        stack.Children.Add(LabeledToolRow("CH4 (t)", _co2Ch4Box));
        stack.Children.Add(LabeledToolRow("N2O (t)", _co2N2oBox));
        _co2Result.FontSize = 11;
        _co2Result.FontFamily = new("Consolas");
        _co2Result.Foreground = LtaiTheme.Sbb(LtaiTheme.AccentSystem);
        _co2Result.Margin = new(0, 2, 0, 0);
        stack.Children.Add(_co2Result);

        var co2Btn = SmallToolButton("Calculate CO2e");
        co2Btn.Click += (_, _) => CalcCO2e();
        stack.Children.Add(co2Btn);

        var hqHeader = new TextBlock
        {
            Text = "Hazard Quotient",
            FontSize = 12,
            FontWeight = FontWeight.Bold,
            Foreground = LtaiTheme.Sbb(LtaiTheme.AccentInfo),
            Margin = new(0, 6, 0, 0)
        };
        stack.Children.Add(hqHeader);

        SetupToolTextBox(_hqExposureBox, "0.01");
        SetupToolTextBox(_hqRfdBox, "0.005");
        stack.Children.Add(LabeledToolRow("Exposure (mg/kg/d)", _hqExposureBox));
        stack.Children.Add(LabeledToolRow("Ref Dose (mg/kg/d)", _hqRfdBox));
        _hqResult.FontSize = 11;
        _hqResult.FontFamily = new("Consolas");
        _hqResult.Foreground = LtaiTheme.Sbb(LtaiTheme.AccentSystem);
        _hqResult.Margin = new(0, 2, 0, 0);
        stack.Children.Add(_hqResult);

        var hqBtn = SmallToolButton("Calculate HQ");
        hqBtn.Click += (_, _) => CalcHQ();
        stack.Children.Add(hqBtn);

        var spHeader = new TextBlock
        {
            Text = "Streeter-Phelps DO Model",
            FontSize = 12,
            FontWeight = FontWeight.Bold,
            Foreground = LtaiTheme.Sbb(LtaiTheme.AccentInfo),
            Margin = new(0, 6, 0, 0)
        };
        stack.Children.Add(spHeader);

        SetupToolTextBox(_spDistBox, "5000");
        SetupToolTextBox(_spVelocityBox, "0.5");
        stack.Children.Add(LabeledToolRow("Distance (m)", _spDistBox));
        stack.Children.Add(LabeledToolRow("Velocity (m/s)", _spVelocityBox));
        _spResult.FontSize = 11;
        _spResult.FontFamily = new("Consolas");
        _spResult.Foreground = LtaiTheme.Sbb(LtaiTheme.AccentSystem);
        _spResult.Margin = new(0, 2, 0, 0);
        stack.Children.Add(_spResult);

        var spBtn = SmallToolButton("Calculate DO Deficit");
        spBtn.Click += (_, _) => CalcStreeterPhelps();
        stack.Children.Add(spBtn);

        var spPadding = new Border { Height = 20 };
        stack.Children.Add(spPadding);

        return new ScrollViewer { Content = new Border { Padding = new(8), Child = stack } };
    }

    private Grid BuildRightPanel()
    {
        var grid = new Grid
        {
            RowDefinitions = new("Auto,*")
        };

        var standardPanel = BuildStandardPanel();
        var reportPanel = BuildReportPanel();

        Grid.SetRow(standardPanel, 0);
        Grid.SetRow(reportPanel, 1);

        grid.Children.Add(standardPanel);
        grid.Children.Add(reportPanel);

        return grid;
    }

    private Border BuildStandardPanel()
    {
        var stack = new StackPanel { Spacing = 6, Margin = new(8) };

        stack.Children.Add(new TextBlock
        {
            Text = "Standard Values Lookup",
            FontSize = 14,
            FontWeight = FontWeight.Bold,
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary)
        });

        _standardCombo.Height = 28;
        _standardCombo.FontSize = 12;
        _standardCombo.Background = LtaiTheme.Sbb(LtaiTheme.BgInput);
        _standardCombo.Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary);
        _standardCombo.Items.Add("GB 3095 (Air)");
        _standardCombo.Items.Add("GB 3838 (Water)");
        _standardCombo.Items.Add("GB 3096 (Noise)");
        _standardCombo.SelectedIndex = 0;
        _standardCombo.SelectionChanged += (_, _) => UpdateStandardDisplay();
        stack.Children.Add(_standardCombo);

        _standardLimits.FontSize = 12;
        _standardLimits.FontFamily = new("Consolas");
        _standardLimits.Foreground = LtaiTheme.Sbb(LtaiTheme.TextSecondary);
        _standardLimits.TextWrapping = TextWrapping.Wrap;
        _standardLimits.Margin = new(0, 4, 0, 0);
        stack.Children.Add(_standardLimits);

        UpdateStandardDisplay();

        return new Border
        {
            Background = LtaiTheme.Sbb(LtaiTheme.BgPanel),
            CornerRadius = new(6),
            Padding = new(8),
            Margin = new(0),
            Child = stack
        };
    }

    private Border BuildReportPanel()
    {
        var stack = new StackPanel { Spacing = 6, Margin = new(8) };

        var headerRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        headerRow.Children.Add(new TextBlock
        {
            Text = "Report Preview",
            FontSize = 14,
            FontWeight = FontWeight.Bold,
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary),
            VerticalAlignment = VerticalAlignment.Center
        });

        var generateBtn = new Button
        {
            Content = "Generate Report",
            Background = LtaiTheme.Sbb(LtaiTheme.AccentDNA),
            Foreground = LtaiTheme.Sbb("#ffffff"),
            FontSize = 11,
            Height = 26,
            CornerRadius = new(4)
        };
        generateBtn.Click += (_, _) => GenerateReport();
        headerRow.Children.Add(generateBtn);

        stack.Children.Add(headerRow);

        _reportBox.IsReadOnly = true;
        _reportBox.AcceptsReturn = true;
        _reportBox.TextWrapping = TextWrapping.Wrap;
        _reportBox.FontFamily = new("Consolas");
        _reportBox.FontSize = 12;
        _reportBox.Background = LtaiTheme.Sbb(LtaiTheme.BgInput);
        _reportBox.Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary);
        _reportBox.MinHeight = 300;

        var reportScroll = new ScrollViewer
        {
            Content = _reportBox,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };

        stack.Children.Add(reportScroll);

        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new(0, 6, 0, 0) };

        var copyBtn = new Button
        {
            Content = "Copy",
            Background = LtaiTheme.Sbb(LtaiTheme.BgPanel),
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextSecondary),
            FontSize = 11,
            Height = 26,
            CornerRadius = new(4),
            Padding = new(12, 0)
        };
        copyBtn.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } cb)
                await cb.SetTextAsync(_reportBox.Text ?? "");
        };
        btnRow.Children.Add(copyBtn);

        var exportBtn = new Button
        {
            Content = "Export",
            Background = LtaiTheme.Sbb(LtaiTheme.AccentSystem),
            Foreground = LtaiTheme.Sbb("#ffffff"),
            FontSize = 11,
            Height = 26,
            CornerRadius = new(4),
            Padding = new(12, 0)
        };
        exportBtn.Click += async (_, _) => await ExportReport();
        btnRow.Children.Add(exportBtn);

        stack.Children.Add(btnRow);

        return new Border
        {
            Background = LtaiTheme.Sbb(LtaiTheme.BgPanel),
            CornerRadius = new(6),
            Padding = new(8),
            Margin = new(0, 8, 0, 0),
            Child = stack
        };
    }

    private void SetupStabilityCombo()
    {
        _stabilityBox.Width = 100;
        _stabilityBox.Height = 26;
        _stabilityBox.FontSize = 12;
        _stabilityBox.Background = LtaiTheme.Sbb(LtaiTheme.BgInput);
        _stabilityBox.Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary);
        _stabilityBox.Items.Add("A (unstable)");
        _stabilityBox.Items.Add("B (mod. unstable)");
        _stabilityBox.Items.Add("C (slight unstable)");
        _stabilityBox.Items.Add("D (neutral)");
        _stabilityBox.Items.Add("E (slight stable)");
        _stabilityBox.Items.Add("F (stable)");
        _stabilityBox.SelectedIndex = 3;
    }

    private static TextBlock SectionHeader(string text) => new()
    {
        Text = text,
        FontSize = 13,
        FontWeight = FontWeight.Bold,
        Foreground = LtaiTheme.Sbb(LtaiTheme.AccentInfo),
        Margin = new(0, 6, 0, 2)
    };

    private static void SetupTextBox(TextBox tb, string defaultVal)
    {
        tb.Width = 80;
        tb.Height = 24;
        tb.FontSize = 12;
        tb.Text = defaultVal;
        tb.Background = LtaiTheme.Sbb(LtaiTheme.BgInput);
        tb.Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary);
    }

    private static void SetupToolTextBox(TextBox tb, string defaultVal)
    {
        tb.Width = 80;
        tb.Height = 22;
        tb.FontSize = 11;
        tb.Text = defaultVal;
        tb.Background = LtaiTheme.Sbb(LtaiTheme.BgInput);
        tb.Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary);
    }

    private static void AddParamRow(StackPanel parent, string label, string unit, TextBox tb)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };

        var lbl = new TextBlock
        {
            Text = label,
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextSecondary),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Width = 110
        };

        var unitLabel = new TextBlock
        {
            Text = unit,
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextDim),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center
        };

        row.Children.Add(lbl);
        row.Children.Add(tb);
        row.Children.Add(unitLabel);
        parent.Children.Add(row);
    }

    private static StackPanel LabeledToolRow(string label, TextBox tb)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        var lbl = new TextBlock
        {
            Text = label,
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextSecondary),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Width = 130
        };
        row.Children.Add(lbl);
        row.Children.Add(tb);
        return row;
    }

    private static Button SmallToolButton(string text) => new()
    {
        Content = text,
        Background = LtaiTheme.Sbb(LtaiTheme.BgPanel),
        Foreground = LtaiTheme.Sbb(LtaiTheme.TextSecondary),
        FontSize = 11,
        Height = 24,
        CornerRadius = new(3),
        Margin = new(0, 2, 0, 0),
        HorizontalAlignment = HorizontalAlignment.Left
    };

    private void UpdateStandardDisplay()
    {
        var key = _standardCombo.SelectedItem?.ToString();
        if (key != null && StandardData.TryGetValue(key, out var data))
            _standardLimits.Text = data;
    }

    private void RunModel()
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== Model Results ===");
        sb.AppendLine();

        var h = ParseDouble(_stackHeightBox.Text);
        var q = ParseDouble(_emissionRateBox.Text);
        var u = ParseDouble(_windSpeedBox.Text);
        var stabClass = _stabilityBox.SelectedIndex;

        if (h.HasValue && q.HasValue && u.HasValue && u.Value > 0)
        {
            var (sigY, sigZ) = CalculateSigma(500, stabClass);
            var plume = GaussianPlume(q.Value, u.Value, sigY, sigZ, 0, h.Value);
            sb.AppendLine($"[Atmospheric] Gaussian Plume at 500m downwind:");
            sb.AppendLine($"  Centerline conc: {plume:F4} [g/m³]");
            sb.AppendLine($"  σy={sigY:F1}m, σz={sigZ:F1}m, u={u:F1}m/s");
        }

        var flow = ParseDouble(_flowRateBox.Text);
        var bod = ParseDouble(_bodBox.Text);
        var doVal = ParseDouble(_doBox.Text);
        var k = ParseDouble(_decayBox.Text);

        if (flow.HasValue && bod.HasValue && doVal.HasValue && k.HasValue)
        {
            var bodLoad = bod.Value * flow.Value * 86.4;
            sb.AppendLine($"[Water Quality]");
            sb.AppendLine($"  BOD loading: {bodLoad:F1} [kg/d]");
            sb.AppendLine($"  DO initial: {doVal:F1} [mg/L]");

            var standardDo = 5.0;
            if (doVal.Value < standardDo)
                sb.AppendLine($"  WARNING: DO below GB 3838 Class III limit ({standardDo} mg/L)");
            if (bod.Value > 4)
                sb.AppendLine($"  WARNING: BOD exceeds GB 3838 Class III limit (4 mg/L) for BOD5");
        }

        var noiseSrc = ParseDouble(_noiseSourceBox.Text);
        var noiseDist = ParseDouble(_noiseDistanceBox.Text);
        var barrierH = ParseDouble(_barrierHeightBox.Text);

        if (noiseSrc.HasValue && noiseDist.HasValue && barrierH.HasValue)
        {
            var distanceAtten = 20 * Math.Log10(noiseDist.Value);
            var barrierAtten = barrierH.Value > 0 ? -5 - 10 * Math.Log10(barrierH.Value + 1) : 0;
            var predicted = noiseSrc.Value - distanceAtten + barrierAtten;
            sb.AppendLine($"[Noise]");
            sb.AppendLine($"  Distance attenuation: {distanceAtten:F1} dB");
            sb.AppendLine($"  Barrier attenuation: {barrierAtten:F1} dB");
            sb.AppendLine($"  Predicted level at receiver: {predicted:F1} dB(A)");

            var noiseStd = 55;
            if (predicted > noiseStd)
                sb.AppendLine($"  WARNING: Exceeds GB 3096 Class 1 residential limit ({noiseStd} dB(A))");
        }

        _resultSummary.Text = sb.ToString();
    }

    private void CalcGaussianPlume()
    {
        var y = ParseDouble(_gpYBox.Text);
        var x = ParseDouble(_gpDistBox.Text);
        var q = ParseDouble(_emissionRateBox.Text);
        var u = ParseDouble(_windSpeedBox.Text);
        var h = ParseDouble(_stackHeightBox.Text);
        var stabClass = _stabilityBox.SelectedIndex;

        if (x.HasValue && q.HasValue && u.HasValue && u.Value > 0 && h.HasValue)
        {
            var (sigY, sigZ) = CalculateSigma(x.Value, stabClass);
            var conc = GaussianPlume(q.Value, u.Value, sigY, sigZ, y ?? 0, h.Value);
            var sb = new StringBuilder();
            sb.AppendLine($"C(x={x:F0}m,y={y:F0}m) = {conc:F6} g/m³");
            sb.AppendLine($"σy={sigY:F1}m, σz={sigZ:F1}m");
            _reportBox.Text = sb.ToString();
        }
    }

    private void CalcCO2e()
    {
        var co2 = ParseDouble(_co2Co2Box.Text) ?? 0;
        var ch4 = ParseDouble(_co2Ch4Box.Text) ?? 0;
        var n2o = ParseDouble(_co2N2oBox.Text) ?? 0;

        var total = co2 * 1 + ch4 * 28 + n2o * 265;
        _co2Result.Text = $"CO2e = {total:F2} t";
    }

    private void CalcHQ()
    {
        var exposure = ParseDouble(_hqExposureBox.Text);
        var rfd = ParseDouble(_hqRfdBox.Text);

        if (exposure.HasValue && rfd.HasValue && rfd.Value > 0)
        {
            var hq = exposure.Value / rfd.Value;
            var risk = hq > 1 ? "Potential risk (HQ > 1)" : "Acceptable (HQ ≤ 1)";
            _hqResult.Text = $"HQ = {hq:F3} — {risk}";
        }
    }

    private void CalcStreeterPhelps()
    {
        var x = ParseDouble(_spDistBox.Text);
        var v = ParseDouble(_spVelocityBox.Text);
        var bod = ParseDouble(_bodBox.Text) ?? 4;
        var doVal = ParseDouble(_doBox.Text) ?? 8;
        var k = ParseDouble(_decayBox.Text) ?? 0.3;

        if (x.HasValue && v.HasValue && v.Value > 0)
        {
            var doSat = 9.0;
            var k1 = k;
            var k2 = 0.4;
            var l0 = bod;
            var d0 = doSat - doVal;
            var t = x.Value / v.Value;
            var tDays = t / 86400;
            var deficit = StreeterPhelpsDO(k1, k2, l0, d0, tDays);

            _spResult.Text = string.Format(CultureInfo.InvariantCulture,
                "DO deficit at {0:F0}m: {1:F3} mg/L\nMin DO: {2:F3} mg/L",
                x.Value, deficit, doSat - deficit);

            var sb = new StringBuilder();
            sb.AppendLine("=== Streeter-Phelps DO Sag Curve ===");
            sb.AppendLine($"Distance: {x:F0} m, Velocity: {v:F2} m/s");
            sb.AppendLine($"Travel time: {tDays:F4} days");
            sb.AppendLine($"k1={k1:F3}/d, k2={k2:F3}/d");
            sb.AppendLine($"Initial BOD (L0): {l0:F2} mg/L");
            sb.AppendLine($"Initial DO deficit (D0): {d0:F2} mg/L");
            sb.AppendLine($"DO deficit at {x:F0}m: {deficit:F3} mg/L");
            sb.AppendLine($"DO at {x:F0}m: {(doSat - deficit):F3} mg/L");
            sb.AppendLine();
            sb.AppendLine("Sag Curve (by distance):");
            for (int dist = 0; dist <= (int)(x.Value); dist += Math.Max(500, (int)(x.Value / 10)))
            {
                var td = (dist / v.Value) / 86400;
                var dd = StreeterPhelpsDO(k1, k2, l0, d0, td);
                var bar = new string('#', Math.Min(40, (int)(dd * 10)));
                sb.AppendLine($"  {dist,6}m | DO deficit: {dd,6:F3} | DO: {(doSat - dd),6:F3} | {bar}");
            }
            _reportBox.Text = sb.ToString();
        }
    }

    private void GenerateReport()
    {
        var sb = new StringBuilder();
        sb.AppendLine("========================================");
        sb.AppendLine("  ENVIRONMENTAL IMPACT ASSESSMENT REPORT");
        sb.AppendLine("========================================");
        sb.AppendLine();

        sb.AppendLine("1. PROJECT OVERVIEW");
        sb.AppendLine("   This report presents environmental impact assessment");
        sb.AppendLine("   results based on modeling parameters entered.");
        sb.AppendLine();

        sb.AppendLine("2. METHODOLOGY");
        sb.AppendLine("   Atmospheric: Gaussian Plume Dispersion Model");
        sb.AppendLine("   Water Quality: BOD/DO mass balance & Streeter-Phelps");
        sb.AppendLine("   Noise: ISO 9613-2 attenuation model");
        sb.AppendLine("   Standards: GB 3095, GB 3838, GB 3096");
        sb.AppendLine();

        sb.AppendLine("3. MODEL RESULTS");
        sb.AppendLine("   " + (_resultSummary.Text ?? "Run model first").Replace("\n", "\n   "));
        sb.AppendLine();

        sb.AppendLine("4. COMPARISON WITH STANDARDS");
        sb.AppendLine("   Referenced Standard: " + (_standardCombo.SelectedItem?.ToString() ?? "N/A"));
        sb.AppendLine("   " + _standardLimits.Text?.Replace("\n", "\n   "));
        sb.AppendLine();

        var noiseSrc = ParseDouble(_noiseSourceBox.Text);
        if (noiseSrc.HasValue)
        {
            if (noiseSrc.Value <= 55)
                sb.AppendLine("   Noise: COMPLIANT with GB 3096 Class 1");
            else if (noiseSrc.Value <= 70)
                sb.AppendLine("   Noise: NON-COMPLIANT with residential limits");
            else
                sb.AppendLine("   Noise: SIGNIFICANTLY EXCEEDS limits");
        }

        var doVal = ParseDouble(_doBox.Text);
        if (doVal.HasValue)
        {
            if (doVal.Value >= 5)
                sb.AppendLine("   DO: COMPLIANT with GB 3838 Class III (>= 5 mg/L)");
            else
                sb.AppendLine("   DO: NON-COMPLIANT with GB 3838 Class III");
        }

        sb.AppendLine();
        sb.AppendLine("5. CONCLUSION");
        sb.AppendLine("   Based on the modeling results and comparison with");
        sb.AppendLine("   applicable environmental quality standards, the");
        sb.AppendLine("   predicted impacts should be evaluated against site-");
        sb.AppendLine("   specific conditions and mitigation measures.");
        sb.AppendLine();
        sb.AppendLine("--- Generated by LTAI EIA Workbench ---");
        sb.AppendLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm"));

        _reportBox.Text = sb.ToString();
    }

    private async Task ExportReport()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export EIA Report",
            DefaultExtension = ".txt",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("Text Files") { Patterns = new[] { "*.txt" } },
                new FilePickerFileType("All Files") { Patterns = new[] { "*.*" } }
            }
        });

        if (file != null)
        {
            await using var stream = await file.OpenWriteAsync();
            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync(_reportBox.Text ?? "");
        }
    }

    private static double GaussianPlume(double q, double u, double sigY, double sigZ, double y, double h)
    {
        if (sigY <= 0 || sigZ <= 0 || u <= 0) return 0;
        var yTerm = Math.Exp(-y * y / (2 * sigY * sigY));
        var zTerm = Math.Exp(-h * h / (2 * sigZ * sigZ));
        return q / (Math.PI * u * sigY * sigZ) * yTerm * zTerm;
    }

    private static (double sigY, double sigZ) CalculateSigma(double x, int stabilityClass)
    {
        var xKm = x / 1000.0;
        if (xKm <= 0) xKm = 0.1;

        double ay, by, az, bz;

        switch (stabilityClass)
        {
            case 0: ay = 0.22; by = 0.894; az = 0.20; bz = 0.914; break;
            case 1: ay = 0.16; by = 0.894; az = 0.12; bz = 0.914; break;
            case 2: ay = 0.11; by = 0.894; az = 0.08; bz = 0.914; break;
            case 3: ay = 0.08; by = 0.894; az = 0.06; bz = 0.914; break;
            case 4: ay = 0.06; by = 0.894; az = 0.045; bz = 0.914; break;
            default: ay = 0.04; by = 0.894; az = 0.025; bz = 0.914; break;
        }

        var sigY = ay * Math.Pow(xKm, by) * 1000;
        var sigZ = az * Math.Pow(xKm, bz) * 1000;

        return (sigY, sigZ);
    }

    private static double StreeterPhelpsDO(double k1, double k2, double l0, double d0, double tDays)
    {
        if (Math.Abs(k2 - k1) < 1e-10)
            return d0 * Math.Exp(-k2 * tDays) + k1 * l0 * tDays * Math.Exp(-k1 * tDays);

        return (k1 * l0) / (k2 - k1) * (Math.Exp(-k1 * tDays) - Math.Exp(-k2 * tDays))
               + d0 * Math.Exp(-k2 * tDays);
    }

    private static double? ParseDouble(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        return double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : null;
    }
}
