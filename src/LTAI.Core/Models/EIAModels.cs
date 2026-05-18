using System.Text.Json;
using System.Text.RegularExpressions;

namespace LTAI.Core.Models;

public static class EIAEngine
{
    public static AtmosphericModels Atmospheric { get; } = new();
    public static WaterQualityModels WaterQuality { get; } = new();
    public static NoiseModels Noise { get; } = new();
    public static SoilGroundwaterModels SoilGroundwater { get; } = new();
    public static EcologicalRiskModels EcologicalRisk { get; } = new();
    public static CarbonGHGModels CarbonGHG { get; } = new();
    public static SolidWasteModels SolidWaste { get; } = new();
    public static SocioeconomicModels Socioeconomic { get; } = new();

    public static Dictionary<string, double> RunAll(Dictionary<string, object> inputs)
    {
        var results = new Dictionary<string, double>();
        try { if (inputs.TryGetValue("plume", out _)) results["plume"] = AtmosphericGaussianPlume(inputs); } catch { }
        try { if (inputs.TryGetValue("do", out _)) results["do"] = WaterQualityDO(inputs); } catch { }
        try { if (inputs.TryGetValue("noise", out _)) results["noise"] = NoiseLevel(inputs); } catch { }
        try { if (inputs.TryGetValue("co2", out _)) results["co2e"] = CarbonCO2Equivalent(inputs); } catch { }
        try { if (inputs.TryGetValue("npv", out _)) results["npv"] = SocioNPV(inputs); } catch { }
        return results;
    }

    private static double AtmosphericGaussianPlume(Dictionary<string, object> inputs) =>
        Atmospheric.GaussianPlume(
            Convert.ToDouble(inputs.GetValueOrDefault("Q", 1.0)),
            Convert.ToDouble(inputs.GetValueOrDefault("u", 2.0)),
            Convert.ToDouble(inputs.GetValueOrDefault("x", 100.0)),
            Convert.ToDouble(inputs.GetValueOrDefault("y", 0.0)),
            Convert.ToDouble(inputs.GetValueOrDefault("z", 0.0)),
            inputs.GetValueOrDefault("stability", "D")?.ToString() ?? "D",
            Convert.ToDouble(inputs.GetValueOrDefault("He", 30.0)));

    private static double WaterQualityDO(Dictionary<string, object> inputs) =>
        WaterQuality.StreeterPhelps(
            Convert.ToDouble(inputs.GetValueOrDefault("DO_sat", 9.0)),
            Convert.ToDouble(inputs.GetValueOrDefault("k1", 0.3)),
            Convert.ToDouble(inputs.GetValueOrDefault("k2", 0.4)),
            Convert.ToDouble(inputs.GetValueOrDefault("L0", 20.0)),
            Convert.ToDouble(inputs.GetValueOrDefault("D0", 2.0)),
            Convert.ToDouble(inputs.GetValueOrDefault("t", 1.0)));

    private static double NoiseLevel(Dictionary<string, object> inputs) =>
        Noise.PointSource(
            Convert.ToDouble(inputs.GetValueOrDefault("Lw", 100.0)),
            Convert.ToDouble(inputs.GetValueOrDefault("r", 50.0)),
            inputs.GetValueOrDefault("ground", "soft")?.ToString() ?? "soft");

    private static double CarbonCO2Equivalent(Dictionary<string, object> inputs)
    {
        var masses = new Dictionary<string, double>();
        if (inputs.TryGetValue("gases", out var gases) && gases is string json)
        {
            var dict = JsonSerializer.Deserialize<Dictionary<string, double>>(json);
            if (dict != null) masses = dict;
        }
        return CarbonGHG.CO2Equivalent(masses);
    }

    private static double SocioNPV(Dictionary<string, object> inputs)
    {
        if (inputs.TryGetValue("cashflow", out var cf) && cf is System.Text.Json.JsonElement je)
        {
            var flows = new List<double>();
            foreach (var e in je.EnumerateArray()) flows.Add(e.GetDouble());
            return Socioeconomic.NPV(flows, Convert.ToDouble(inputs.GetValueOrDefault("rate", 0.05)));
        }
        return 0;
    }
}

public class AtmosphericModels
{
    private static readonly Dictionary<string, (double a, double b)> PgSigmaY = new()
    {
        ["A"] = (0.901074, 1.0), ["B"] = (0.914370, 0.5), ["C"] = (0.924279, 0.0),
        ["D"] = (0.929418, -0.5), ["E"] = (0.920818, -0.8), ["F"] = (0.896864, -1.0)
    };

    private static readonly Dictionary<string, (double a, double b)> PgSigmaZ = new()
    {
        ["A"] = (1.12154, 2.0), ["B"] = (0.941015, 0.5), ["C"] = (0.917595, 0.0),
        ["D"] = (0.826212, -0.5), ["E"] = (0.788370, -0.8), ["F"] = (0.762401, -1.0)
    };

    private static readonly Dictionary<string, double> WindExponent = new()
    {
        ["A"] = 0.10, ["B"] = 0.15, ["C"] = 0.20, ["D"] = 0.25, ["E"] = 0.30, ["F"] = 0.30
    };

    private double SigmaY(double x, string stability) => SigmaXY(x, stability, PgSigmaY, (0.929418, 0.0));
    private double SigmaZ(double x, string stability) => SigmaXY(x, stability, PgSigmaZ, (0.826212, -0.5));

    private static double SigmaXY(double x, string stability,
        Dictionary<string, (double a, double b)> table, (double a, double b) fallback)
    {
        var (a, b) = table.GetValueOrDefault(stability, fallback);
        return a * Math.Pow(x, b);
    }

    public double GaussianPlume(double Q, double u, double x, double y, double z,
        string stability = "D", double He = 30)
    {
        if (Q <= 0 || u <= 0 || x <= 0) return 0;
        var sy = SigmaY(x, stability);
        var sz = SigmaZ(x, stability);
        if (sy <= 0 || sz <= 0) return 0;
        var denom = 2 * Math.PI * u * sy * sz;
        var expY = Math.Exp(-y * y / (2 * sy * sy));
        var expZ1 = Math.Exp(-(z - He) * (z - He) / (2 * sz * sz));
        var expZ2 = Math.Exp(-(z + He) * (z + He) / (2 * sz * sz));
        return Q / denom * expY * (expZ1 + expZ2);
    }

    public double BriggsPlumeRise(double Ts, double Ta, double Vs, double D, double u, double Fb = 0)
    {
        if (u <= 0 || Ts <= 0) return 0;
        if (Fb <= 0)
        {
            if (Ts <= Ta) Fb = 0.01;
            else Fb = 9.81 * Vs * D * D * (Ts - Ta) / (4 * Ts);
        }
        var xf = Fb >= 55 ? 50 * Math.Pow(Fb, 0.625) : 14 * Math.Pow(Fb, 0.4);
        return 1.6 * Math.Pow(Fb, 1.0 / 3) * Math.Pow(xf, 2.0 / 3) / u;
    }

    public double BuildingDownwash(double Hb, double Hbw, double He)
    {
        if (Hb <= 0) return 1.0;
        var Hc = Hb + 1.5 * Hbw;
        if (He < Hc) return 2.0;
        if (He < Hb + 2.5 * Hbw) return 1.5;
        return 1.0;
    }

    public double WindProfile(double uRef, double zRef, double z, string stability = "D", bool isUrban = true)
    {
        var p = WindExponent.GetValueOrDefault(stability, 0.25) + (isUrban ? 0.05 : 0.0);
        return uRef * Math.Pow(z / zRef, p);
    }

    public double DryDeposition(double C, double Vd) => C * Vd / 1000.0;

    public double AveragingTimeCorrection(double T1, double T2, double p = 0.2) =>
        Math.Pow(T1 / T2, p);

    public double ChemicalDecay(double C0, double t, double halfLife) =>
        halfLife <= 0 ? C0 : C0 * Math.Exp(-0.693147 * t / halfLife);

    public double GaussianPuff(double Q, double u, double x, double y, double z,
        double sx, double sy, double sz)
    {
        if (u <= 0) return 0;
        var denom = Math.Pow(2 * Math.PI, 1.5) * sx * sy * sz;
        return Q / denom * Math.Exp(-0.5 * x * x / (sx * sx))
               * Math.Exp(-0.5 * y * y / (sy * sy))
               * Math.Exp(-0.5 * z * z / (sz * sz));
    }
}

public class WaterQualityModels
{
    public double StreeterPhelps(double DOsat, double k1, double k2, double L0, double D0, double t)
    {
        if (Math.Abs(k2 - k1) < 0.0001)
            return DOsat - (k1 * L0 * t * Math.Exp(-k1 * t) + D0 * Math.Exp(-k1 * t));
        var deficit = k1 * L0 / (k2 - k1) * (Math.Exp(-k1 * t) - Math.Exp(-k2 * t)) + D0 * Math.Exp(-k2 * t);
        return DOsat - deficit;
    }

    public double BODDecay(double L0, double k1, double t) => L0 * Math.Exp(-k1 * t);

    public double Nitrification(double NH3_0, double kn, double t) => NH3_0 * Math.Exp(-kn * t);

    public double DOSaturation(double T, double elevationM = 0)
    {
        var Tk = T + 273.15;
        var lnDO = -139.34411 + 1.575701e5 / Tk - 6.642308e7 / (Tk * Tk)
                   + 1.243800e10 / (Tk * Tk * Tk) - 8.621949e11 / (Tk * Tk * Tk * Tk);
        return Math.Exp(lnDO) * (1 - 0.0001148 * elevationM);
    }

    public double ReaerationRate(double u, double H, string method = "oconnor")
    {
        var denom = Math.Pow(H, method == "churchill" ? 1.673 : 1.5) + 0.001;
        return method == "churchill"
            ? 5.026 * Math.Pow(u, 0.969) / denom
            : 3.93 * Math.Sqrt(u) / denom;
    }

    public double TemperatureCorrection(double k20, double T, double theta = 1.047) =>
        k20 * Math.Pow(theta, T - 20);

    public double RiverMixing2D(double M, double Q, double x, double y, double u, double H)
    {
        if (u <= 0 || H <= 0 || x <= 0) return 0;
        var Ey = 0.6 * H * Math.Sqrt(9.81 * H * 0.001);
        var front = M / (u * H * Math.Sqrt(4 * Math.PI * Ey * x / u));
        return front * Math.Exp(-u * y * y / (4 * Ey * x));
    }

    public double EutrophicationScore(double TP, double TN, double Chla, double SD)
    {
        var tsiTp = TP > 0 ? 14.42 * Math.Log(TP * 1000) + 4.15 : 0;
        var tsiChla = Chla > 0 ? 9.81 * Math.Log(Chla) + 30.6 : 0;
        var tsiSd = SD > 0 ? 60 - 14.41 * Math.Log(SD) : 0;
        return (tsiTp + tsiChla + tsiSd) / 3;
    }
}

public class NoiseModels
{
    private static readonly Dictionary<int, double> AWeighting = new()
    {
        [63] = -26.2, [125] = -16.1, [250] = -8.6, [500] = -3.2,
        [1000] = 0.0, [2000] = 1.2, [4000] = 1.0, [8000] = -1.1
    };

    private double GroundAttenuation(double r, string groundType) => groundType switch
    {
        "soft" => 5 * (1 - Math.Exp(-r / 50)),
        "mixed" => 3 * (1 - Math.Exp(-r / 100)),
        _ => 0.0
    };

    public double PointSource(double Lw, double r, string groundType = "soft")
    {
        r = Math.Max(r, 1);
        return Lw - 20 * Math.Log10(r) - 11 - GroundAttenuation(r, groundType);
    }

    public double BarrierMaekawa(double N) =>
        N < 0 ? 0 : 10 * Math.Log10(3 + 20 * N);

    public double AirAbsorption(double r, double freq, double T = 20, double RH = 70)
    {
        if (freq <= 0) return 0;
        var alpha = (1.84e-11 * freq * freq * Math.Sqrt(T) + 0.01275 * freq * Math.Sqrt(RH / 100)) * 0.001;
        return alpha * r;
    }

    public double AWeight(Dictionary<int, double> loctave)
    {
        double sum = 0;
        foreach (var (freq, level) in loctave)
        {
            var ai = AWeighting.GetValueOrDefault(freq, 0.0);
            sum += Math.Pow(10, (level + ai) / 10);
        }
        return sum == 0 ? 0 : 10 * Math.Log10(sum);
    }

    public double TrafficFHWA(double V, double D, string mix = "auto")
    {
        if (V <= 0 || D <= 0) return 0;
        var L0 = mix switch { "medium_truck" => 45.0, "heavy_truck" => 50.0, _ => 38.0 };
        return L0 + 10 * Math.Log10(V / D);
    }

    public double Superposition(List<double> levels) =>
        levels.Count == 0 ? 0 : 10 * Math.Log10(levels.Sum(l => Math.Pow(10, l / 10)));
}

public class SoilGroundwaterModels
{
    public double DarcyVelocity(double K, double dh, double dl, double ne = 0.25) =>
        ne <= 0 || dl <= 0 ? 0 : K * (dh / dl) / ne;

    public double SoluteTransport1D(double C0, double x, double t, double v, double Dx)
    {
        if (Dx <= 0 || t <= 0) return 0;
        return C0 / (2 * Math.Sqrt(Math.PI * Dx * t)) * Math.Exp(-(x - v * t) * (x - v * t) / (4 * Dx * t));
    }

    public double RetardationFactor(double pb, double n, double Kd) =>
        n <= 0 ? 1 : 1 + (pb / n) * Kd;

    public double FreundlichIsotherm(double Ce, double Kf, double nf) =>
        Ce <= 0 ? 0 : Kf * Math.Pow(Ce, 1.0 / nf);

    public double LangmuirIsotherm(double Ce, double qmax, double b) =>
        Ce <= 0 ? 0 : qmax * b * Ce / (1 + b * Ce);

    public double FirstOrderDecay(double C0, double k, double t) => C0 * Math.Exp(-k * t);
}

public class EcologicalRiskModels
{
    public double HazardQuotient(double exposure, double referenceDose) =>
        referenceDose <= 0 ? 0 : exposure / referenceDose;

    public double RiskCharRatio(double PEC, double PNEC) =>
        PNEC <= 0 ? 0 : PEC / PNEC;

    public double HC5FromSSD(List<double> ssdValues)
    {
        var pos = ssdValues.Where(v => v > 0).ToList();
        if (ssdValues.Count < 5 || pos.Count < 3) return 0;

        var logs = pos.Select(v => Math.Log(v)).ToList();
        var mean = logs.Average();
        var std = Math.Sqrt(logs.Sum(v => (v - mean) * (v - mean)) / (logs.Count - 1));
        return Math.Exp(mean - 1.645 * std);
    }

    public double Bioaccumulation(double BCF, double Cwater) => BCF * Cwater;

    public double FoodChainMultiplier(double BAF, int trophicLevel = 2) =>
        BAF <= 0 ? 0 : Math.Pow(BAF, trophicLevel);

    public double RiskIndex(List<double> hqValues) => hqValues.Sum();
}

public class CarbonGHGModels
{
    private static readonly Dictionary<string, double> Gwp100 = new()
    {
        ["CO2"] = 1, ["CH4"] = 28, ["N2O"] = 265, ["SF6"] = 23500,
        ["HFC-134a"] = 1300, ["PFC-14"] = 6630, ["NF3"] = 16100
    };

    public double CO2Equivalent(Dictionary<string, double> masses)
    {
        double total = 0;
        foreach (var (gas, mass) in masses)
            total += mass * Gwp100.GetValueOrDefault(gas, 1);
        return total;
    }

    public double StationaryCombustion(double fuelTons, double efKgTj, double NCV) =>
        fuelTons * efKgTj * NCV / 1000.0;

    public double MobileCombustion(double distanceKm, double efGKm) =>
        distanceKm * efGKm / 1_000_000.0;

    public double FugitiveEmission(double activity, double efKgUnit) =>
        activity * efKgUnit / 1000.0;

    public string ScopeClassify(string sourceType)
    {
        var s = sourceType.ToLowerInvariant();
        if (s.Contains("combustion") || s.Contains("process") || s.Contains("fugitive") || s.Contains("vehicle"))
            return "Scope 1 (直接排放)";
        if (s.Contains("electricity") || s.Contains("purchased_energy") || s.Contains("steam"))
            return "Scope 2 (间接能源排放)";
        return "Scope 3 (其他间接排放)";
    }

    public double BiogenicCarbon(double biomassTons, double carbonFraction = 0.5) =>
        biomassTons * carbonFraction * (44.0 / 12.0);
}

public class SolidWasteModels
{
    public double LandGEMCH4(double L0, double k, double c, double t)
    {
        if (c > t) return 0;
        return Math.Max(0, L0 * (Math.Exp(-k * c) - Math.Exp(-k * t)));
    }

    public double LeachateQuantity(double P, double A, double runoffCoeff = 0.5) =>
        P * A * (1 - runoffCoeff) / 1000.0;

    public double DecompositionRate(double k, double T, double Tref = 20) =>
        k * Math.Pow(1.047, T - Tref);

    public double IncinerationEmission(double wasteTons, double efMgKg) =>
        wasteTons * efMgKg * 1e-6;

    public double FlyAshStabilization(double ashTons, double cementRatio = 0.15) =>
        ashTons * cementRatio;

    public bool WasteCompatibility(double pHA, double pHB, bool reactiveA = false)
    {
        if (reactiveA && pHB < 5) return false;
        if (Math.Abs(pHA - pHB) > 4) return false;
        return true;
    }
}

public class SocioeconomicModels
{
    public double ExponentialGrowth(double P0, double r, double t) => P0 * Math.Exp(r * t);

    public double LogisticGrowth(double P0, double K, double r, double t) =>
        P0 <= 0 || K <= 0 ? 0 : K / (1 + (K / P0 - 1) * Math.Exp(-r * t));

    public Dictionary<string, double> TripGeneration(double households, double ratePerHH,
        Dictionary<string, double>? modeSplit = null)
    {
        var split = modeSplit ?? new() { ["auto"] = 0.7, ["transit"] = 0.2, ["walk_bike"] = 0.1 };
        var total = households * ratePerHH;
        return split.ToDictionary(kv => kv.Key, kv => total * kv.Value);
    }

    public double NPV(List<double> cashflow, double discountRate)
    {
        double npv = 0;
        for (int t = 0; t < cashflow.Count; t++)
            npv += cashflow[t] / Math.Pow(1 + discountRate, t);
        return npv;
    }

    public double CostBenefitRatio(List<double> benefits, List<double> costs, double discountRate)
    {
        double pvB = 0, pvC = 0;
        for (int t = 0; t < Math.Max(benefits.Count, costs.Count); t++)
        {
            if (t < benefits.Count) pvB += benefits[t] / Math.Pow(1 + discountRate, t);
            if (t < costs.Count) pvC += costs[t] / Math.Pow(1 + discountRate, t);
        }
        return pvC <= 0 ? double.PositiveInfinity : pvB / pvC;
    }

    public double DisabilityAdjustedLifeYears(double incidence, double duration,
        double disabilityWeight, double mortality = 0) =>
        mortality * 75 + incidence * duration * disabilityWeight;

    public double LandUseChangeIntensity(double beforeArea, double afterArea) =>
        beforeArea <= 0 ? 0 : (afterArea - beforeArea) / beforeArea;
}
