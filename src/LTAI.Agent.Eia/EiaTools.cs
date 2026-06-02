using System.ComponentModel;
using LTAI.AI;

namespace LTAI.Agent.Tools;

/// <summary>
/// Environmental Impact Assessment (EIA) tools implementing Chinese standards:
/// GB 3095-2012, HJ 633-2012, GB 3096-2008, GB 3838-2002, GB/T 3840-1991.
/// </summary>
[ToolDomain("eia")]
public sealed class EiaTools
{
    // ─── Air Quality Standards (GB 3095-2012 / HJ 633-2012) ───

    private static readonly Dictionary<string, double[]> AirQualityLimits = new()
    {
        ["SO2"]  = [50, 150, 475, 800],    // 1-hour avg: I, II, III, IV
        ["NO2"]  = [40, 80, 180, 280],
        ["PM10"] = [50, 150, 250, 350],
        ["PM25"] = [35, 75, 115, 150],
        ["O3"]   = [100, 160, 215, 265],
        ["CO"]   = [5, 10, 35, 60],         // mg/m³
    };

    [Description("按 GB 3095-2012 / HJ 633-2012 中国 AQI 标准分类空气质量。输入 SO2/NO2/PM10/PM2.5/O3/CO 浓度。\n"
        + "适用场景：评估空气质量等级、判断是否适用于户外活动、环评报告中的空气质量评价。\n"
        + "不适用场景：噪声评价（请用 ClassifyNoise）、水质评价（请用 ClassifyWaterQuality）。\n"
        + "关键参数：so2/no2/pm10/pm25 — 污染物浓度(µg/m³)；o3/co — 可选。")]
    [ToolExample("SO2 50 NO2 80 PM10 150 PM2.5 75 空气质量算几级")]
    [ToolExample("今天的空气质量怎么样")]
    public static string ClassifyAirQuality(
        [Description("SO2 concentration (µg/m³)")] double so2,
        [Description("NO2 concentration (µg/m³)")] double no2,
        [Description("PM10 concentration (µg/m³)")] double pm10,
        [Description("PM2.5 concentration (µg/m³)")] double pm25,
        [Description("O3 concentration (µg/m³), optional")] double o3 = 0,
        [Description("CO concentration (mg/m³), optional")] double co = 0)
    {
        var pollutants = new Dictionary<string, double>
        {
            ["SO2"] = so2, ["NO2"] = no2, ["PM10"] = pm10, ["PM25"] = pm25,
            ["O3"] = o3, ["CO"] = co
        };

        var maxIaqi = 0;
        var worstPollutant = "";
        var details = new System.Text.StringBuilder();
        details.AppendLine("## Air Quality Classification (HJ 633-2012)\n");
        details.AppendLine("| Pollutant | Value | IAQI | Level |");
        details.AppendLine("|-----------|-------|------|-------|");

        foreach (var (name, value) in pollutants)
        {
            if (value <= 0 || !AirQualityLimits.TryGetValue(name, out var limits)) continue;

            var iaqi = CalcIAQI(value, limits);
            if (iaqi > maxIaqi) { maxIaqi = iaqi; worstPollutant = name; }

            var level = iaqi switch
            {
                <= 50 => "I (优)",
                <= 100 => "II (良)",
                <= 150 => "III (轻度污染)",
                <= 200 => "IV (中度污染)",
                <= 300 => "V (重度污染)",
                _ => "VI (严重污染)"
            };
            details.AppendLine($"| {name} | {value:F1} | {iaqi} | {level} |");
        }

        var overallLevel = maxIaqi switch
        {
            <= 50 => "I (优) — Green",
            <= 100 => "II (良) — Yellow",
            <= 150 => "III (轻度污染) — Orange",
            <= 200 => "IV (中度污染) — Red",
            <= 300 => "V (重度污染) — Purple",
            _ => "VI (严重污染) — Maroon"
        };

        details.AppendLine($"\n**Overall AQI: {maxIaqi} — {overallLevel}**");
        details.AppendLine($"**Worst pollutant: {worstPollutant}**");
        return details.ToString();
    }

    private static int CalcIAQI(double value, double[] limits)
    {
        // BP_i values: [0, 50, 100, 150, 200, 300, 400]
        double[] bp = [0, 50, 100, 150, 200, 300, 400];
        double[] concBp = [0, .. limits];

        if (value <= 0) return 0;
        if (value >= concBp[^1]) return 400;

        for (int i = 1; i < concBp.Length; i++)
        {
            if (value <= concBp[i])
            {
                return (int)Math.Round(
                    (bp[i] - bp[i - 1]) / (concBp[i] - concBp[i - 1])
                    * (value - concBp[i - 1]) + bp[i - 1]);
            }
        }
        return 400;
    }

    // ─── Noise Classification (GB 3096-2008) ───

    private static readonly Dictionary<string, (double day, double night)> NoiseLimits = new()
    {
        ["0 (疗养区)"]     = (50, 40),
        ["1 (居住文教)"]   = (55, 45),
        ["2 (商住混合)"]   = (60, 50),
        ["3 (工业区)"]     = (65, 55),
        ["4a (交通干线)"]  = (70, 55),
        ["4b (铁路干线)"]  = (70, 60),
    };

    [Description("按 GB 3096-2008 标准分类环境噪声等级。输入昼间和夜间分贝值。\n"
        + "适用场景：评估噪声是否达标、环评中的噪声评价、判断噪声适用区域类别。\n"
        + "不适用场景：空气质量评价（请用 ClassifyAirQuality）、水质评价（请用 ClassifyWaterQuality）。\n"
        + "关键参数：dayLeq — 昼间等效声级 dB(A)；nightLeq — 夜间等效声级 dB(A)。")]
    [ToolExample("白天 55 分贝晚上 45 分贝噪声超标吗")]
    public static string ClassifyNoise(
        [Description("Daytime noise level Leq dB(A)")] double dayLeq,
        [Description("Nighttime noise level Leq dB(A)")] double nightLeq)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("## Environmental Noise Classification (GB 3096-2008)\n");
        sb.AppendLine($"| Standard Zone | Day ≤{dayLeq:F1} | Night ≤{nightLeq:F1} |");
        sb.AppendLine("|---------------|----------|------------|");

        var compliant = "";
        foreach (var (zone, (dLimit, nLimit)) in NoiseLimits)
        {
            var dOk = dayLeq <= dLimit ? "✅" : "❌";
            var nOk = nightLeq <= nLimit ? "✅" : "❌";
            sb.AppendLine($"| {zone} | {dLimit} dB {dOk} | {nLimit} dB {nOk} |");
            if (dayLeq <= dLimit && nightLeq <= nLimit && string.IsNullOrEmpty(compliant))
                compliant = zone;
        }

        if (!string.IsNullOrEmpty(compliant))
            sb.AppendLine($"\n**Compliant with: {compliant}**");
        else
            sb.AppendLine("\n**Exceeds all standard limits**");

        return sb.ToString();
    }

    // ─── Water Quality Classification (GB 3838-2002) ───

    [Description("按 GB 3838-2002 标准分类地表水水质。输入 DO/COD/NH3-N/TP 参数。\n"
        + "适用场景：评估地表水水质类别、环评中的水环境评价、判断水体适用功能。\n"
        + "不适用场景：空气质量评价（请用 ClassifyAirQuality）、噪声评价（请用 ClassifyNoise）。\n"
        + "关键参数：do_mgL — 溶解氧(mg/L)；cod — 化学需氧量(mg/L)；nh3n — 氨氮(mg/L)；tp — 总磷(mg/L)。")]
    [ToolExample("DO 6.5 COD 15 NH3-N 0.5 TP 0.1 这水是几类")]
    public static string ClassifyWaterQuality(
        [Description("DO (dissolved oxygen, mg/L)")] double do_mgL,
        [Description("COD (chemical oxygen demand, mg/L)")] double cod,
        [Description("NH3-N (ammonia nitrogen, mg/L)")] double nh3n,
        [Description("TP (total phosphorus, mg/L)")] double tp)
    {
        // GB 3838-2002 limits
        var limits = new (string name, double val, double[] std)[]
        {
            ("DO",      do_mgL,  [7.5, 6, 5, 3, 2]),    // ≥ (higher is better)
            ("COD",     cod,     [15, 15, 20, 30, 40]),   // ≤
            ("NH3-N",   nh3n,    [0.15, 0.5, 1.0, 1.5, 2.0]), // ≤
            ("TP",      tp,      [0.02, 0.1, 0.2, 0.3, 0.4]), // ≤
        };

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("## Surface Water Quality Classification (GB 3838-2002)\n");
        sb.AppendLine("| Parameter | Value | Class |");
        sb.AppendLine("|-----------|-------|-------|");

        var worst = 0;
        foreach (var (name, val, std) in limits)
        {
            var cls = ClassifyWaterParam(name, val, std);
            sb.AppendLine($"| {name} | {val:F2} | {cls} |");
            worst = Math.Max(worst, cls);
        }

        var overall = worst switch
        {
            1 => "I (本源水) — Excellent",
            2 => "II (饮用水源) — Good",
            3 => "III (渔业用水) — Fair",
            4 => "IV (工业用水) — Poor",
            5 => "V (农业用水) — Bad",
            _ => "劣V — Unusable"
        };
        sb.AppendLine($"\n**Overall: Class {worst} — {overall}**");
        return sb.ToString();
    }

    private static int ClassifyWaterParam(string name, double val, double[] std)
    {
        // DO: ≥ limits (higher is better)
        if (name == "DO")
        {
            if (val >= std[0]) return 1;
            if (val >= std[1]) return 2;
            if (val >= std[2]) return 3;
            if (val >= std[3]) return 4;
            if (val >= std[4]) return 5;
            return 6;
        }

        // Other parameters: ≤ limits (lower is better)
        if (val <= std[0]) return 1;
        if (val <= std[1]) return 2;
        if (val <= std[2]) return 3;
        if (val <= std[3]) return 4;
        if (val <= std[4]) return 5;
        return 6;
    }

    // ─── Gaussian Plume Dispersion (GB/T 3840-1991) ───

    [Description("高斯烟羽大气扩散模型。按 GB/T 3840-1991 计算下风向污染物浓度。\n"
        + "适用场景：预测污染源下风向浓度、环评中的大气扩散模拟、评估排放影响范围。\n"
        + "不适用场景：室内空气质量评价、空气质量分类（请用 ClassifyAirQuality）。\n"
        + "关键参数：q — 排放速率(g/s)；u — 风速(m/s)；h — 有效源高(m)；x — 下风向距离(m)。")]
    [ToolExample("排放速率 100g/s 风速 3m/s 源高 50m 下风向 500m 浓度多少")]
    public static string GaussianPlume(
        [Description("Emission rate (g/s)")] double q,
        [Description("Wind speed (m/s)")] double u,
        [Description("Effective stack height (m)")] double h,
        [Description("Downwind distance (m)")] double x,
        [Description("Stability class (A-F, default D)")] string stability = "D",
        [Description("Crosswind distance (m), 0 = centerline")] double y = 0)
    {
        // Pasquill-Gifford dispersion parameters
        var (a1, a2, b1, b2) = stability.ToUpperInvariant() switch
        {
            "A" => (0.22, 0.20, 0.0001, 2.0),
            "B" => (0.16, 0.12, 0.0001, 1.5),
            "C" => (0.11, 0.08, 0.0001, 1.0),
            "D" => (0.08, 0.06, 0.0001, 0.5),
            "E" => (0.06, 0.03, 0.0001, 0.3),
            "F" => (0.04, 0.016, 0.0001, 0.2),
            _   => (0.08, 0.06, 0.0001, 0.5),
        };

        if (x <= 0) return "Error: Downwind distance must be > 0";
        if (u <= 0) return "Error: Wind speed must be > 0";

        var sigmaY = a1 * x / Math.Sqrt(1 + b1 * x);
        var sigmaZ = a2 * x / Math.Sqrt(1 + b2 * x);

        var conc = q / (2 * Math.PI * u * sigmaY * sigmaZ)
                 * Math.Exp(-0.5 * Math.Pow(y / sigmaY, 2))
                 * Math.Exp(-0.5 * Math.Pow(h / sigmaZ, 2));

        // Convert to µg/m³
        var concUg = conc * 1_000_000;

        var stabilityNames = new Dictionary<string, string>
        {
            ["A"] = "强不稳定", ["B"] = "不稳定", ["C"] = "弱不稳定",
            ["D"] = "中性", ["E"] = "弱稳定", ["F"] = "稳定"
        };

        return $"""
            ## Gaussian Plume Model (GB/T 3840-1991)

            | Parameter | Value |
            |-----------|-------|
            | Emission Q | {q:F4} g/s |
            | Wind speed u | {u:F1} m/s |
            | Stack height H | {h:F1} m |
            | Downwind x | {x:F0} m |
            | Crosswind y | {y:F0} m |
            | Stability | {stability} ({stabilityNames.GetValueOrDefault(stability, "中性")}) |
            | σy | {sigmaY:F1} m |
            | σz | {sigmaZ:F1} m |
            | Concentration C(x,y,0) | {concUg:F4} µg/m³ |
            """;
    }

    // ─── CO2 Equivalent ───

    private static readonly Dictionary<string, double> GwpFactors = new()
    {
        ["CO2"]  = 1,
        ["CH4"]  = 28,    // AR5, 100-year
        ["N2O"]  = 265,
        ["SF6"]  = 23500,
        ["CF4"]  = 6630,  // PFC-14
        ["C2F6"] = 11100, // PFC-116
        ["HFC134a"] = 1300,
    };

    [Description("计算温室气体的 CO₂ 当量排放。按 IPCC GWP 100 年指标。\n"
        + "适用场景：计算碳排放总量、温室气体排放报告、碳足迹评估。\n"
        + "不适用场景：空气质量分类、高斯扩散模拟。\n"
        + "关键参数：gas — 气体名称(CO2/CH4/N2O/SF6等)；massKg — 质量(kg)。")]
    [ToolExample("CH4 排放 100kg 二氧化碳当量是多少")]
    public static string CO2Equivalent(
        [Description("Gas name: CO2, CH4, N2O, SF6, CF4, C2F6, HFC134a")] string gas,
        [Description("Mass in kg")] double massKg)
    {
        if (!GwpFactors.TryGetValue(gas, out var gwp))
            return $"Error: Unknown gas '{gas}'. Supported: {string.Join(", ", GwpFactors.Keys)}";

        var co2e = massKg * gwp;
        return $"""
            ## CO₂ Equivalent Calculation

            | Parameter | Value |
            |-----------|-------|
            | Gas | {gas} |
            | Mass | {massKg:F2} kg |
            | GWP (100-yr) | {gwp} |
            | CO₂-eq | {co2e:F2} kg CO₂-eq |
            | Equivalent | {co2e / 1000:F2} tonnes CO₂-eq |
            """;
    }

    // ─── Hazard Quotient (Ecological Risk) ───

    [Description("计算生态风险评价的危害商数(HQ)。HQ = 实测浓度 / PNEC 安全阈值。\n"
        + "适用场景：生态风险评估、污染物毒性评价、环境安全边界判断。\n"
        + "关键参数：concentration — 实测浓度(mg/kg 或 mg/L)；pnec — PNEC 安全阈值。")]
    [ToolExample("实测浓度 10mg/kg PNEC 2mg/kg 风险高吗")]
    public static string HazardQuotient(
        [Description("Measured concentration (mg/kg or mg/L)")] double concentration,
        [Description("PNEC or safe threshold (mg/kg or mg/L)")] double pnec)
    {
        if (pnec <= 0) return "Error: PNEC must be > 0";

        var hq = concentration / pnec;

        var risk = hq switch
        {
            < 0.1 => "Negligible risk",
            < 1.0 => "Low risk — acceptable",
            < 10 => "Moderate risk — further assessment needed",
            _ => "High risk — mitigation required"
        };

        return $"""
            ## Hazard Quotient

            | Parameter | Value |
            |-----------|-------|
            | Concentration | {concentration:F4} |
            | PNEC | {pnec:F4} |
            | HQ = C/PNEC | {hq:F4} |
            | Risk Level | **{risk}** |
            """;
    }

    // ─── Standard Lookup (Chinese Environmental Standards) ───

    private static readonly Dictionary<string, string> Standards = new()
    {
        ["GB 3095-2012"] = "Ambient Air Quality Standards / 环境空气质量标准\n" +
            "Classes: I (自然保护区), II (居住/商业/工业)\n" +
            "Key limits (Class II, annual): PM2.5 ≤ 35µg/m³, PM10 ≤ 70µg/m³, SO2 ≤ 60µg/m³, NO2 ≤ 40µg/m³",

        ["HJ 633-2012"] = "Technical Regulation on AQI / 环境空气质量指数(AQI)技术规定\n" +
            "AQI = max(IAQI). Levels: 0-50(I), 51-100(II), 101-150(III), 151-200(IV), 201-300(V), >300(VI)",

        ["GB 3096-2008"] = "Environmental Noise Standards / 声环境质量标准\n" +
            "Zones: 0(50/40), 1(55/45), 2(60/50), 3(65/55), 4a(70/55), 4b(70/60) dB day/night",

        ["GB 3838-2002"] = "Surface Water Quality Standards / 地表水环境质量标准\n" +
            "Classes I-V: I(本源), II(饮用水源), III(渔业), IV(工业), V(农业)",

        ["GB/T 3840-1991"] = "Technical guidelines for air dispersion modeling / 大气扩散模式技术导则\n" +
            "Gaussian plume model, Pasquill stability classes A-F",

        ["GB 16297-1996"] = "Comprehensive Emission Standard of Air Pollutants / 大气污染物综合排放标准",

        ["GB 8978-1996"] = "Integrated Wastewater Discharge Standard / 污水综合排放标准",

        ["HJ 2.2-2018"] = "Technical Guidelines for Environmental Impact Assessment — Atmospheric / 环境影响评价技术导则 大气环境\n" +
            "Replaces HJ 2.2-2008. AERMOD recommended model.",

        ["HJ 2.4-2021"] = "Technical Guidelines for Environmental Impact Assessment — Noise / 环境影响评价技术导则 声环境\n" +
            "Replaces HJ 2.4-2009.",

        ["GB 12348-2008"] = "Emission Standards for Industrial Noise / 工业企业厂界环境噪声排放标准\n" +
            "Day: 65dB(I)/60dB(II)/55dB(III)/50dB(IV), Night: 55/50/45/40",

        ["GB 22337-2008"] = "Social Life Noise Emission Standards / 社会生活环境噪声排放标准",

        ["HJ 2035-2013"] = "Technical Guidelines for Solid Waste Management / 固体废物处理处置工程技术导则",
    };

    [Description("查询中国环境标准(GB/HJ 标准号)。返回标准名称、等级划分、限值等信息。\n"
        + "适用场景：查询某个环评标准的具体限值、确认标准适用范围。\n"
        + "关键参数：code — 标准号如 GB 3095-2012 或 HJ 633-2012。")]
    [ToolExample("查一下 GB 3095-2012 的标准")]
    [ToolExample("HJ 633-2012 是什么标准")]
    public static string LookupStandard(
        [Description("Standard code like 'GB 3095-2012' or 'HJ 633-2012'")] string code)
    {
        // Normalize: remove dots, dashes, spaces
        var key = code.Trim().ToUpperInvariant();

        // Try exact match
        if (Standards.TryGetValue(key, out var desc))
            return $"## {key}\n\n{desc}";

        // Try fuzzy match
        var matches = Standards.Keys
            .Where(k => k.Contains(key) || key.Contains(k))
            .ToList();

        if (matches.Count == 1)
            return $"## {matches[0]}\n\n{Standards[matches[0]]}";

        if (matches.Count > 1)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Multiple standards matching '{code}':\n");
            foreach (var m in matches)
                sb.AppendLine($"- {m}: {Standards[m].Split('\n')[0]}");
            return sb.ToString();
        }

        return $"Standard '{code}' not found. Available: {string.Join(", ", Standards.Keys.Take(10))}...";
    }
}
