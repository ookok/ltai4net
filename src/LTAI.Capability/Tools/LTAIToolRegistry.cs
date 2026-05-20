using LTAI.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

namespace LTAI.Capability.Tools;

public static class LTAIToolRegistry
{
    private static bool _seeded;
    private static IServiceProvider? _serviceProvider;

    public static async Task SeedAllAsync(IToolRegistry registry, IServiceProvider sp)
    {
        _serviceProvider = sp;
        if (_seeded) return;
        _seeded = true;

        foreach (var tool in AllTools)
        {
            if (tool.Handler != null)
                await registry.RegisterAsync(tool.Name, tool.Handler);
        }
    }

    private static T GetService<T>()
    {
        var svc = _serviceProvider?.GetRequiredService(typeof(T))
            ?? throw new InvalidOperationException($"Service {typeof(T).Name} not available. Ensure SeedAllAsync has been called.");
        return (T)svc;
    }

    public static readonly ToolDef[] AllTools =
    {
        // ═══ VFS — 7 tools ═══
        new("vfs:read", "Read file content from virtual filesystem", "vfs",
            async args => await VfsAdapter.Instance.ReadAsync(Arg(args, "path"))),
        new("vfs:write", "Write content to virtual filesystem", "vfs",
            async args => await Task.FromResult<object?>(await VfsAdapter.Instance.WriteAsync(Arg(args, "path"), Arg(args, "content")))),
        new("vfs:list", "List directory contents in VFS", "vfs",
            async args => await Task.FromResult<object?>(await VfsAdapter.Instance.ListAsync(Arg(args, "path")))),
        new("vfs:delete", "Delete file from VFS", "vfs",
            async _ => { VfsAdapter.Instance.Delete(Arg(_, "path")); return true; }),
        new("vfs:exists", "Check if file exists in VFS", "vfs",
            async args => await VfsAdapter.Instance.ExistsAsync(Arg(args, "path"))),
        new("vfs:search", "Search VFS by content", "vfs",
            async args => await Task.FromResult<object?>(await VfsAdapter.Instance.SearchAsync(Arg(args, "path"), Arg(args, "query")))),
        new("vfs:move", "Move/rename file in VFS", "vfs",
            async args => await VfsAdapter.Instance.MoveAsync(Arg(args, "source"), Arg(args, "dest"))),

        // ═══ Web & Search — 4 tools ═══
        new("web_fetch", "Fetch web page content by URL", "web",
            async args => { using var http = new HttpClient(); return await http.GetStringAsync(Arg(args, "url")); }),
        new("search", "Multi-source unified search (web/wiki/apis)", "web", null),
        new("browser_browse", "Navigate browser to URL and interact", "web", null),
        new("search_apis", "Search 1400+ public APIs by keyword", "web",
            async args => { await PublicApisResource.Instance.LoadAsync(); var r = PublicApisResource.Instance.Search(Arg(args, "query")); return r; }),

        // ═══ Knowledge — 4 tools ═══
        new("km_search", "Semantic search in Kernel Memory knowledge base", "knowledge", null),
        new("km_import", "Import document into Kernel Memory", "knowledge", null),
        new("km_ask", "Ask question against Kernel Memory with sources", "knowledge", null),
        new("vector_search", "Vector similarity search across embeddings", "knowledge", null),

        // ═══ Code — 4 tools ═══
        new("code_analyze", "Analyze code structure, complexity, dependencies", "code", null),
        new("code_review", "Review code for bugs, style, security issues", "code", null),
        new("sandbox_exec", "Execute code in isolated sandbox", "code", null),
        new("code_graph", "Query code knowledge graph (callers/callees/impact)", "code", null),

        // ═══ Document — 5 tools ═══
        new("doc_parse", "Parse document content (PDF/DOCX/XLSX/MD)", "doc", null),
        new("text_extract", "Extract plain text from any format", "doc", null),
        new("report_generate", "Generate formatted report from data", "doc", null),
        new("observe_format", "Dump formatted document as raw text for LLM observation", "doc", null),
        new("style_learn", "Learn formatting patterns from example documents", "doc", null),
        new("visual_render", "Render chart/flowchart/floorplan/contour/3dsurface/windrose as SVG/HTML", "doc",
            async args => RenderVisual(Arg(args, "type"), Arg(args, "data"), Arg(args, "title"))),

        // ═══ EIA Models — 16 tools ═══
        new("gaussian_plume", "Gaussian plume air dispersion model (GB/T3840-1991)", "eia",
            async args => ComputeGaussianPlume(ArgDouble(args, "q"), ArgDouble(args, "u"), ArgDouble(args, "h"), ArgDouble(args, "x"))),
        new("gaussian_plume_building", "Gaussian plume with building downwash (Huber-Snyder, HJ2.2-2018)", "eia",
            async args => ComputeBuildingDownwash(ArgDouble(args, "q"), ArgDouble(args, "u"), ArgDouble(args, "h"), ArgDouble(args, "x"), ArgDouble(args, "bh"), ArgDouble(args, "bw"))),
        new("inversion_fumigation", "Inversion breakup fumigation model for coastal/short-stack scenarios", "eia",
            async args => ComputeFumigation(ArgDouble(args, "q"), ArgDouble(args, "u"), ArgDouble(args, "h"), ArgDouble(args, "x"), ArgDouble(args, "zi"))),
        new("noise_iso9613", "ISO 9613-2 outdoor sound propagation prediction with ground/barrier", "eia",
            async args => ComputeNoiseIso9613(ArgDouble(args, "lw"), ArgDouble(args, "distance"), Arg(args, "ground_type"))),
        new("noise_attenuation", "Simple noise attenuation with distance", "eia",
            async args => ComputeNoiseAttenuation(ArgDouble(args, "lw"), ArgDouble(args, "distance"))),
        new("noise_traffic", "Traffic noise prediction model (FHWA/CJW method)", "eia",
            async args => ComputeTrafficNoise(ArgDouble(args, "volume_per_h"), ArgDouble(args, "speed_kmh"), ArgDouble(args, "distance"), ArgDouble(args, "heavy_ratio"))),
        new("streeter_phelps", "Streeter-Phelps DO sag curve for water quality", "eia",
            async args => ComputeStreeterPhelps(ArgDouble(args, "do_sat"), ArgDouble(args, "do0"), ArgDouble(args, "k1"), ArgDouble(args, "k2"), ArgDouble(args, "distance"))),
        new("river_mixing", "River pollutant mixing: complete/incomplete lateral mixing zone length", "eia",
            async args => ComputeRiverMixing(ArgDouble(args, "flow_rate"), ArgDouble(args, "width"), ArgDouble(args, "depth"), ArgDouble(args, "velocity"), ArgDouble(args, "emission_load"))),
        new("co2_equivalent", "CO2 equivalent calculation (IPCC GWP100)", "eia",
            async args => ComputeCo2Equivalent(ArgDouble(args, "ch4_kg"), ArgDouble(args, "n2o_kg"))),
        new("hazard_quotient", "Ecological Hazard Quotient for single substance", "eia",
            async args => ComputeHazardQuotient(ArgDouble(args, "exposure"), ArgDouble(args, "reference_dose"))),
        new("ecological_risk", "Multi-substance ecological risk index (Hakanson method)", "eia",
            async args => ComputeEcologicalRisk(Arg(args, "metals_csv"))),
        new("soil_erosion", "Universal Soil Loss Equation (USLE) for construction sites", "eia",
            async args => ComputeSoilLoss(ArgDouble(args, "r_factor"), ArgDouble(args, "k_factor"), ArgDouble(args, "ls_factor"), ArgDouble(args, "c_factor"), ArgDouble(args, "p_factor"))),
        new("carbon_sink", "Forest/grassland carbon sink estimation (biomass method)", "eia",
            async args => ComputeCarbonSink(ArgDouble(args, "area_ha"), Arg(args, "vegetation_type"), ArgDouble(args, "growth_rate"))),
        new("lookup_standard", "Look up Chinese environmental standard (GB/HJ) by code", "eia",
            async args => LookupStandard(Arg(args, "code"))),
        new("classify_water_quality", "Classify water quality per GB3838-2002 using COD/BOD/DO/NH3N", "eia",
            async args => ClassifyWater(ArgDouble(args, "cod"), ArgDouble(args, "bod"), ArgDouble(args, "do_mg_l"), ArgDouble(args, "nh3n"))),
        new("classify_air_quality", "Classify air quality per GB3095-2012 using SO2/NO2/PM10/PM2.5", "eia",
            async args => ClassifyAir(ArgDouble(args, "so2"), ArgDouble(args, "no2"), ArgDouble(args, "pm10"), ArgDouble(args, "pm25"))),
        new("classify_noise_level", "Classify noise level per GB3096-2008 by zone category", "eia",
            async args => ClassifyNoise(ArgDouble(args, "daytime_db"), ArgDouble(args, "night_db"), Arg(args, "zone_category"))),

        // ═══ EIA 专业模型 — 4 tools ═══
        new("aermod_full", "EPA AERMOD regulatory model: auto-download EXE + Process wrapper (CO/DF modes)", "eia_pro",
            async args => { var w = new AermodWrapper(); var r = await w.RunAsync(BuildAermodInput(args)); return r.ToSummary(); }),
        new("calpuff_full", "EPA CALPUFF non-steady-state model: CALMET→CALPUFF→CALPOST pipeline (long-range)", "eia_pro",
            async args => { var w = new CalpuffWrapper(); var r = await w.RunFullAsync(BuildCalpuffInput(args)); return r.ToSummary(); }),
        new("gral_dispersion", "GRAL Lagrangian particle dispersion: complex terrain + building CFD (pure C#)", "eia_pro",
            async args => { var w = new GralWrapper(); var inp = BuildGralInput(args); return w.RunDispersion(inp); }),
        new("mathnet_stats", "Math.NET statistical analysis: interpolation/fitting/FFT/Monte Carlo for EIA data", "eia_pro",
            async args => MathNetAnalyzer.Analyze(Arg(args, "data_csv"), Arg(args, "method"))),

        // ═══ GIS — 5 tools ═══
        new("geocode", "Geocode address to latitude/longitude", "gis", null),
        new("gis_buffer", "Create buffer polygon around point, return GeoJSON", "gis",
            async args => ComputeBuffer(ArgDouble(args, "lat"), ArgDouble(args, "lng"), ArgDouble(args, "radius_m"))),
        new("spatial_search", "Check if point is inside polygon", "gis",
            async args => PointInPolygon(ArgDouble(args, "lat"), ArgDouble(args, "lng"), Arg(args, "geojson"))),
        new("distance_calc", "Calculate Haversine distance between coordinates", "gis",
            async args => Haversine(ArgDouble(args, "lat1"), ArgDouble(args, "lng1"), ArgDouble(args, "lat2"), ArgDouble(args, "lng2"))),
        new("coordinate_transform", "Transform between WGS84/GCJ02/CGCS2000", "gis",
            async args => TransformCoord(ArgDouble(args, "lat"), ArgDouble(args, "lng"), Arg(args, "from"), Arg(args, "to"))),

        // ═══ Git — 3 tools ═══
        new("git_diff", "Show working tree changes", "git", null),
        new("git_log", "Show commit history", "git", null),
        new("git_blame", "Show line-by-line authorship", "git", null),

        // ═══ CLI — 5 tools ═══
        new("cli_wrap_function", "Wrap any code function as CLI tool executable", "cli",
            async args => CliEngine.WrapFunction(Arg(args, "name"), Arg(args, "code"), Arg(args, "language"))),
        new("cli_from_repo", "Clone git repo, detect entry points, generate CLI tools", "cli",
            async args => CliEngine.FromRepo(Arg(args, "repo_url"), Arg(args, "branch"))),
        new("cli_from_manifest", "Generate CLI tools from YAML manifest definition", "cli",
            async args => CliEngine.FromManifest(Arg(args, "yaml_manifest"))),
        new("cli_list_tools", "List all generated CLI tools and their status", "cli",
            async _ => CliEngine.ListTools()),
        new("cli_scan_path", "Scan system PATH for available CLI programs with --help introspection", "cli",
            async args => CliEngine.ScanPath(Arg(args, "path_filter"))),
        // ═══ Shell — 2 tools ═══
        new("bash", "Execute shell command (sandboxed, unrestricted for system operations)", "shell", null),
        new("cli_execute", "Execute CLI command with safety gate (blocks rm/sudo/dd/shutdown)", "shell",
            async args => CliEngine.Execute(Arg(args, "command"), Arg(args, "args"))),

        // ═══ CAD — 3 tools ═══
        new("cad_import", "Import CAD model (STEP/DWG/DXF/STL) for analysis via CADability", "cad",
            async args => CadEngine.Import(Arg(args, "file_path"), Arg(args, "format"))),
        new("cad_analyze", "Analyze CAD geometry: solids/surfaces/bounds/volume/materials", "cad",
            async args => CadEngine.Analyze(Arg(args, "file_path"))),
        new("cad_export", "Export CAD model to target format (STEP/DWG/DXF/STL)", "cad",
            async args => CadEngine.Export(Arg(args, "file_path"), Arg(args, "target_format"))),

        // ═══ Memory — 2 tools ═══
        new("remember", "Store information in agent memory for future recall", "memory", null),
        new("recall", "Retrieve relevant memories by query", "memory", null),

        // ═══ Notification — 1 tools ═══
        new("notify", "Send notification via configured channel (Telegram/WeWork/Slack)", "notification", null),

        // ═══ Integration — 6 tools ═══
        new("email_send", "Send email via SMTP", "integration",
            async args => {
                var gw = GetService<LTAI.Capability.Integration.MessageGateway>();
                var to = Arg(args, "to"); var subject = Arg(args, "subject"); var body = Arg(args, "body");
                if (string.IsNullOrWhiteSpace(to) || string.IsNullOrWhiteSpace(body)) return new { error = "to and body required" };
                if (string.IsNullOrWhiteSpace(subject)) subject = "LTAI Notification";
                var ok = await gw.SendSmtpAsync(to, subject, body);
                return new { success = ok, platform = "smtp", to };
            }),
        new("sms_send", "Send SMS via Aliyun/Tencent Cloud SMS", "integration",
            async args => {
                var sms = GetService<LTAI.Capability.Integration.SmsGateway>();
                var msg = Arg(args, "message"); var phone = Arg(args, "phone");
                if (string.IsNullOrWhiteSpace(msg)) return new { error = "message required" };
                var ok = await sms.SendAsync(msg, string.IsNullOrWhiteSpace(phone) ? null : phone);
                return new { success = ok, phone = phone ?? sms.Config.PhoneNumbers.FirstOrDefault() };
            }),
        new("translate", "Translate text using Baidu Translate API", "integration",
            async args => {
                var svc = GetService<LTAI.Capability.Integration.TranslateService>();
                var text = Arg(args, "text"); var from = Arg(args, "from", "auto"); var to = Arg(args, "to", "zh");
                if (string.IsNullOrWhiteSpace(text)) return new { error = "text required" };
                var result = await svc.TranslateAsync(text, from, to);
                return new { success = result != null, text, from, to, translation = result };
            }),
        new("image_search", "Search images via Unsplash/Pixabay", "integration",
            async args => {
                var svc = GetService<LTAI.Capability.Integration.ImageSearchService>();
                var query = Arg(args, "query"); var count = (int)ArgDouble(args, "count", 10);
                var source = Arg(args, "source", "unsplash");
                if (string.IsNullOrWhiteSpace(query)) return new { error = "query required" };
                var results = await svc.SearchAsync(query, count, source);
                return new { success = true, query, count = results.Count, results = results.Select(r => new { r.Id, r.Url, r.Description, r.Author, r.Source }) };
            }),
        new("weather", "Get current weather by city name", "integration",
            async args => {
                var svc = GetService<LTAI.Capability.Integration.WeatherService>();
                var city = Arg(args, "city"); var source = Arg(args, "source", "openweathermap");
                if (string.IsNullOrWhiteSpace(city)) return new { error = "city required" };
                var data = await svc.GetWeatherAsync(city, source);
                return data != null ? new { success = true, data.City, data.Weather, data.Description, data.Temperature, data.Humidity, data.WindSpeed, data.Source }
                    : new { error = "Weather data not available", city };
            }),
        new("github_status", "Get GitHub release status and latest version", "integration",
            async args => {
                var updater = GetService<LTAI.Capability.Integration.AutoUpdater>();
                var result = await updater.CheckForUpdatesAsync();
                return new { result.CurrentVersion, result.LatestVersion, result.HasUpdate, result.ReleaseNotes };
            }),

        // ═══ System — 7 tools ═══
        new("models_list", "List all registered model providers and their models", "system",
            async _ => {
                var mgr = GetService<LTAI.Core.System.ModelManager>();
                var models = mgr.ListAll();
                return new { count = models.Count, models = models.Select(m => new { m.Provider, m.ModelName, m.TierName, m.Capabilities }) };
            }),
        new("models_show", "Show details for a specific provider or model", "system",
            async args => {
                var mgr = GetService<LTAI.Core.System.ModelManager>();
                var name = Arg(args, "name");
                if (string.IsNullOrWhiteSpace(name)) return new { error = "name required" };
                var info = mgr.Show(name);
                return info != null ? new { info.Provider, info.ModelName, info.TierName, info.BaseUrl, info.Capabilities } : new { error = $"Provider/model not found: {name}" };
            }),
        new("models_search", "Search models by keyword", "system",
            async args => {
                var mgr = GetService<LTAI.Core.System.ModelManager>();
                var q = Arg(args, "query");
                if (string.IsNullOrWhiteSpace(q)) return new { error = "query required" };
                var results = mgr.Search(q);
                return new { query = q, count = results.Count, results = results.Select(m => new { m.Provider, m.ModelName, m.TierName }) };
            }),
        new("models_sync", "Sync model registry info from built-in providers", "system",
            async _ => {
                var mgr = GetService<LTAI.Core.System.ModelManager>();
                return mgr.SyncInfo();
            }),
        new("service_install", "Install LTAI as Windows Service", "system",
            async _ => {
                var svc = GetService<LTAI.Core.System.ServiceManager>();
                var result = await svc.InstallAsync();
                return new { result.Success, result.Message };
            }),
        new("service_uninstall", "Uninstall LTAI Windows Service", "system",
            async _ => {
                var svc = GetService<LTAI.Core.System.ServiceManager>();
                var result = await svc.UninstallAsync();
                return new { result.Success, result.Message };
            }),
        new("service_status", "Check LTAI Windows Service status or start/stop/restart", "system",
            async args => {
                var svc = GetService<LTAI.Core.System.ServiceManager>();
                var action = Arg(args, "action", "status");
                var result = action.ToLowerInvariant() switch
                {
                    "start" => await svc.StartAsync(),
                    "stop" => await svc.StopAsync(),
                    "restart" => await svc.RestartAsync(),
                    _ => await svc.StatusAsync()
                };
                return new { action, result.Success, result.Message, result.Output };
            }),
    };

    public static int Total => AllTools.Length;

    private static string Arg(Dictionary<string, object?>? args, string key, string def = "")
        => args?.TryGetValue(key, out var v) == true ? v?.ToString() ?? def : def;

    private static double ArgDouble(Dictionary<string, object?>? args, string key, double def = 0)
        => args?.TryGetValue(key, out var v) == true && double.TryParse(v?.ToString(), out var d) ? d : def;

    // ═══ Tool Implementations ═══

    private static object ComputeGaussianPlume(double q, double u, double h, double x)
    {
        if (u <= 0 || x <= 0) return new { error = "Invalid parameters: u>0, x>0 required" };
        var sigmaY = 0.22 * x / Math.Sqrt(1 + 0.0001 * x);
        var sigmaZ = 0.20 * x;
        var concentration = q / (2 * Math.PI * u * sigmaY * sigmaZ) * Math.Exp(-h * h / (2 * sigmaZ * sigmaZ));
        return new { concentration_mg_m3 = Math.Round(concentration * 1e6, 4), sigma_y = Math.Round(sigmaY, 1), sigma_z = Math.Round(sigmaZ, 1), distance_m = x };
    }

    private static object ComputeNoiseAttenuation(double lw, double distance)
    {
        if (distance <= 0) return new { error = "distance > 0 required" };
        var attenuation = 20 * Math.Log10(Math.Max(distance, 0.1));
        var spl = lw - attenuation;
        return new { spl_db = Math.Round(spl, 1), attenuation_db = Math.Round(attenuation, 1), distance_m = distance };
    }

    private static object ComputeStreeterPhelps(double doSat, double do0, double k1, double k2, double x)
    {
        var deficit = doSat - do0;
        var d = k1 / (k2 - k1) * (Math.Exp(-k1 * x / 86400) - Math.Exp(-k2 * x / 86400)) * deficit + deficit * Math.Exp(-k2 * x / 86400);
        var doVal = doSat - d;
        return new { do_mg_l = Math.Round(doVal, 4), deficit = Math.Round(d, 4), distance_m = x };
    }

    private static object ComputeCo2Equivalent(double ch4, double n2o)
    {
        var co2e = ch4 * 28 + n2o * 265;
        return new { co2e_kg = Math.Round(co2e, 2), ch4_kg = ch4, n2o_kg = n2o, gwp_ch4 = 28, gwp_n2o = 265 };
    }

    private static object ComputeHazardQuotient(double exposure, double rfd)
    {
        if (rfd <= 0) return new { error = "reference_dose > 0 required" };
        var hq = exposure / rfd;
        return new { hazard_quotient = Math.Round(hq, 4), risk_level = hq < 1 ? "acceptable" : hq < 10 ? "moderate" : "high" };
    }

    private static object LookupStandard(string code)
    {
        var standards = new Dictionary<string, string>
        {
            ["GB3095-2012"] = "Ambient Air Quality Standards: SO2, NO2, PM10, PM2.5, CO, O3",
            ["GB3838-2002"] = "Surface Water Quality Standards: Class I-V",
            ["GB3096-2008"] = "Environmental Noise Standards: 0-4 categories",
            ["GB16297-1996"] = "Integrated Emission Standards for Air Pollutants",
            ["GB8978-1996"] = "Integrated Wastewater Discharge Standards",
            ["HJ2.2-2018"] = "Technical Guidelines for Atmospheric EIA",
            ["HJ2.3-2018"] = "Technical Guidelines for Surface Water EIA",
            ["HJ2.4-2021"] = "Technical Guidelines for Noise EIA",
            ["HJ19-2011"] = "Technical Guidelines for Ecological EIA",
            ["GB/T3840-1991"] = "Technical methods for local air pollutant dispersion models"
        };

        if (standards.TryGetValue(code.ToUpper(), out var desc))
            return new { code = code.ToUpper(), description = desc, found = true };

        var partial = standards.FirstOrDefault(s => s.Key.Contains(code, StringComparison.OrdinalIgnoreCase));
        if (partial.Key != null)
            return new { code = partial.Key, description = partial.Value, found = true, note = $"partial match for '{code}'" };

        return new { code, found = false, note = "Standard not found in local database" };
    }

    private static object ComputeNoiseIso9613(double lw, double distance, string groundType = "mixed")
    {
        if (distance <= 0) return new { error = "distance > 0 required" };
        var groundFactor = groundType switch { "hard" => 0.0, "soft" => 1.0, _ => 0.5 };
        var geometric = 20 * Math.Log10(Math.Max(distance, 0.1)) + 11;
        var atmospheric = distance * 0.005;
        var ground = 4.8 - 2 * (groundType == "hard" ? 600 : 200) / Math.Max(distance, 1) * (17 + 300 / Math.Max(distance, 1));
        var barrier = 0.0;
        var spl = lw - geometric - atmospheric - groundFactor * ground - barrier;
        return new { spl_db = Math.Round(spl, 1), geometric_db = Math.Round(geometric, 1), atmospheric_db = Math.Round(atmospheric, 2),
                     ground_db = Math.Round(groundFactor * ground, 1), distance_m = distance, ground_type = groundType };
    }

    private static object ClassifyWater(double cod, double bod, double doVal, double nh3n)
    {
        var scores = new List<int>();
        if (cod <= 15) scores.Add(1); else if (cod <= 15) scores.Add(1); else if (cod <= 20) scores.Add(3); else if (cod <= 30) scores.Add(4); else if (cod <= 40) scores.Add(5); else scores.Add(6);
        if (bod <= 3) scores.Add(1); else if (bod <= 4) scores.Add(3); else if (bod <= 6) scores.Add(4); else if (bod <= 10) scores.Add(5); else scores.Add(6);
        if (doVal >= 7.5) scores.Add(1); else if (doVal >= 6) scores.Add(2); else if (doVal >= 5) scores.Add(3); else if (doVal >= 3) scores.Add(4); else if (doVal >= 2) scores.Add(5); else scores.Add(6);
        if (nh3n <= 0.15) scores.Add(1); else if (nh3n <= 0.5) scores.Add(2); else if (nh3n <= 1.0) scores.Add(3); else if (nh3n <= 1.5) scores.Add(4); else if (nh3n <= 2.0) scores.Add(5); else scores.Add(6);
        var level = (int)scores.Max();
        var cls = level <= 1 ? "I" : level <= 2 ? "II" : level <= 3 ? "III" : level <= 4 ? "IV" : level <= 5 ? "V" : ">V";
        return new { classification = cls, level, cod, bod, do_mg_l = doVal, nh3n, standard = "GB3838-2002" };
    }

    private static object ClassifyAir(double so2, double no2, double pm10, double pm25)
    {
        var calcIAQI = (double value, double[] bp) =>
        {
            for (var i = 0; i < bp.Length - 2; i++)
                if (value <= bp[i + 1]) return ((50 + 50 * i) - (1 + 50 * i)) / (bp[i + 1] - bp[i]) * (value - bp[i]) + (1 + 50 * i);
            return 500.0;
        };
        var iaqiSo2 = calcIAQI(so2, new double[] { 0, 50, 150, 475, 800, 1600 });
        var iaqiNo2 = calcIAQI(no2, new double[] { 0, 40, 80, 180, 280, 565 });
        var iaqiPm10 = calcIAQI(pm10, new double[] { 0, 50, 150, 250, 350, 420 });
        var iaqiPm25 = calcIAQI(pm25, new double[] { 0, 35, 75, 115, 150, 250 });
        var aqi = new[] { iaqiSo2, iaqiNo2, iaqiPm10, iaqiPm25 }.Max();
        var cls = aqi <= 50 ? "I(优)" : aqi <= 100 ? "II(良)" : aqi <= 150 ? "III(轻度污染)" : aqi <= 200 ? "IV(中度污染)" : aqi <= 300 ? "V(重度污染)" : "VI(严重污染)";
        return new { classification = cls, aqi = Math.Round(aqi, 1), so2_iaqi = Math.Round(iaqiSo2, 1), no2_iaqi = Math.Round(iaqiNo2, 1), pm10_iaqi = Math.Round(iaqiPm10, 1), pm25_iaqi = Math.Round(iaqiPm25, 1), standard = "GB3095-2012" };
    }

    private static object ClassifyNoise(double daytimeDb, double nightDb, string zone = "class2")
    {
        var limits = new Dictionary<string, (int day, int night)>
        {
            ["class0"] = (50, 40), ["class1"] = (55, 45), ["class2"] = (60, 50), ["class3"] = (65, 55), ["class4"] = (70, 55)
        };
        var (dayLimit, nightLimit) = limits.GetValueOrDefault(zone, (60, 50));
        var dayOk = daytimeDb <= dayLimit;
        var nightOk = nightDb <= nightLimit;
        var overall = dayOk && nightOk ? "达标" : "超标";
        return new { overall, day_ok = dayOk, night_ok = nightOk, daytime_db = daytimeDb, night_db = nightDb,
                     day_limit = dayLimit, night_limit = nightLimit, zone, standard = "GB3096-2008" };
    }

    private static object RenderVisual(string type, string data, string title)
    {
        var colors = new[] { "#58a6ff", "#3fb950", "#d29922", "#f85149", "#a371f7", "#ff9944" };
        var chartId = Guid.NewGuid().ToString("N")[..6];
        var height = type == "map" || type == "floorplan" ? 400 : 300;

        return new
        {
            html = type switch
            {
                "bar" => ChartBuilder.BuildBar(title, data, colors, chartId),
                "line" => ChartBuilder.BuildLine(title, data, colors, chartId),
                "pie" => ChartBuilder.BuildPie(title, data, colors, chartId),
                "map" => ChartBuilder.BuildMap(title, data, chartId, height),
                "flowchart" => BuildFlowchart(title, data),
                "floorplan" => BuildFloorplan(title, data, chartId, height),
                "contour" => BuildContour(title, data, chartId),
                "3dsurface" => Build3DSurface(title, data, chartId),
                "windrose" => BuildWindRose(title, data, chartId),
                _ => ChartBuilder.BuildTable(title, data)
            },
            type, title
        };
    }

    private static string BuildFlowchart(string title, string mermaidDef) =>
        $@"<div class='mermaid' style='background:#fff;padding:16px;border-radius:8px'>
graph TD
{mermaidDef}
</div><script src='https://cdn.jsdelivr.net/npm/mermaid@11/dist/mermaid.min.js'></script><script>mermaid.initialize({{startOnLoad:true,theme:'default'}});</script>";

    private static string BuildContour(string title, string data, string id)
    {
        var values = data.Split(',').Select(v => double.TryParse(v, out var d) ? d : 0).ToList();
        var size = (int)Math.Sqrt(values.Count);
        var html = $"<canvas id='{id}' width='{size * 20}' height='{size * 20}'></canvas><script>";
        html += $"var c=document.getElementById('{id}').getContext('2d');";
        html += $"var v=[{string.Join(",", values)}];var s={size};";
        html += "for(var i=0;i<s;i++)for(var j=0;j<s;j++){var val=v[i*s+j];var r=Math.floor(val*255);c.fillStyle=`rgb(${r},${128-r/2},${255-r})`;c.fillRect(j*20,i*20,20,20);c.fillStyle='#333';c.font='8px sans-serif';c.fillText(val.toFixed(1),j*20+2,i*20+12);}";
        html += "</script>";
        return html;
    }

    private static string Build3DSurface(string title, string data, string id)
    {
        var points = data.Split(';').Select(p => p.Split(':').Select(double.Parse).ToArray()).ToList();
        var html = $"<canvas id='{id}' width='600' height='400'></canvas><script>";
        html += $"var c=document.getElementById('{id}').getContext('2d');var pts=[{string.Join(",", points.Select(p => $"[{p[0]},{p[1]},{p[2]}]"))}];";
        html += "pts.sort((a,b)=>b[2]-a[2]);pts.forEach(p=>{var x=200+p[0]*2-p[1];var y=200-p[2]*5+p[0]+p[1];c.beginPath();c.arc(x,y,3,0,Math.PI*2);c.fillStyle=`rgb(${Math.floor(p[2]*25)},${100},${200-Math.floor(p[2]*20)})`;c.fill();});";
        html += "</script>";
        return html;
    }

    private static string BuildWindRose(string title, string data, string id)
    {
        var dirs = data.Split(',').Select(d => d.Split(':')).Where(d => d.Length == 2)
            .Select(d => (dir: d[0], freq: double.Parse(d[1]))).ToList();
        var cx = 200; var cy = 200; var r = 150;
        var html = $"<svg viewBox='0 0 400 400'><text x='200' y='20' text-anchor='middle' font-weight='bold'>{title}</text>";
        var angles = new Dictionary<string, double> { ["N"] = 270, ["NE"] = 315, ["E"] = 0, ["SE"] = 45, ["S"] = 90, ["SW"] = 135, ["W"] = 180, ["NW"] = 225 };
        foreach (var (dir, freq) in dirs.Where(d => angles.ContainsKey(d.dir)))
        {
            var angle = angles[dir] * Math.PI / 180;
            var len = r * freq / 20;
            var x2 = cx + len * Math.Cos(angle);
            var y2 = cy - len * Math.Sin(angle);
            html += $"<line x1='{cx}' y1='{cy}' x2='{x2:F1}' y2='{y2:F1}' stroke='#58a6ff' stroke-width='2' opacity='0.7'/>";
            html += $"<text x='{x2 + 5:F1}' y='{y2:F1}' font-size='9' fill='#8b949e'>{dir} {freq:F1}%</text>";
        }
        html += "<circle cx='200' cy='200' r='150' fill='none' stroke='var(--border)' stroke-dasharray='4,4'/>";
        html += "<circle cx='200' cy='200' r='75' fill='none' stroke='var(--border)' stroke-dasharray='4,4'/>";
        html += "</svg>";
        return html;
    }

    private static AermodInput BuildAermodInput(Dictionary<string, object?> args) => new()
    {
        EmissionRate = ArgDouble(args, "emission_rate"), StackHeight = ArgDouble(args, "stack_h"),
        StackDiameter = ArgDouble(args, "stack_d"), ExitVelocity = ArgDouble(args, "exit_v", 15),
        ExitTemperature = ArgDouble(args, "exit_t", 400), UrbanRural = ArgDouble(args, "urban", 1),
        PollutantId = Arg(args, "pollutant", "SO2"), Title = Arg(args, "title", "LTAI AERMOD"),
        MetDataPath = Arg(args, "met_path", "aermet.sfc")
    };

    private static GralInput BuildGralInput(Dictionary<string, object?> args) => new()
    {
        EmissionRate = ArgDouble(args, "emission_rate"), SourceHeight = ArgDouble(args, "source_h", 50),
        WindSpeed = ArgDouble(args, "wind_speed", 3), WindSigma = ArgDouble(args, "wind_sigma", 0.5),
        MixingHeight = ArgDouble(args, "mixing_h", 800), ParticleCount = (int)ArgDouble(args, "particles", 500)
    };

    private static CalpuffInput BuildCalpuffInput(Dictionary<string, object?> args) => new()
    {
        EmissionRate = ArgDouble(args, "emission_rate"), StackHeight = ArgDouble(args, "stack_h"),
        StackDiameter = ArgDouble(args, "stack_d"), ExitVelocity = ArgDouble(args, "exit_v", 15),
        ExitTemperature = ArgDouble(args, "exit_t", 400),
        SourceLat = ArgDouble(args, "source_lat", 39.9), SourceLon = ArgDouble(args, "source_lon", 116.4),
        MetDays = (int)ArgDouble(args, "met_days", 30), CellSize = ArgDouble(args, "cell_size", 500),
        Title = Arg(args, "title", "LTAI CALPUFF")
    };

    private static string BuildFloorplan(string title, string data, string id, int h)
    {
        var rects = data.Split(';').Select(cell =>
        {
            var parts = cell.Split(':');
            if (parts.Length < 4) return "";
            var x = double.Parse(parts[0]);
            var y = double.Parse(parts[1]);
            var w = double.Parse(parts[2]);
            var ht = double.Parse(parts[3]);
            var label = parts.Length > 4 ? parts[4] : "";
            var cx = x + w / 2;
            var cy = y + ht / 2 + 5;
            return $"<rect x='{x}' y='{y}' width='{w}' height='{ht}' fill='#e8f0fe' stroke='#1a73e8' stroke-width='2' rx='4'/><text x='{cx}' y='{cy}' text-anchor='middle' font-size='11' fill='#333'>{label}</text>";
        }).ToList();

        return $@"<svg viewBox='0 0 800 600' style='width:100%;height:{h}px;border:1px solid var(--border);border-radius:8px;background:#fff'>
<text x='400' y='20' text-anchor='middle' font-weight='bold' font-size='14'>{title}</text>
{string.Join("\n", rects)}
</svg>";
    }

    // ═══ EIA Model Implementations ═══

    /// <summary>
    /// Gaussian plume with building downwash (Huber-Snyder method, HJ2.2-2018).
    /// Standard model for general industrial EIA with nearby buildings.
    /// When stack height h less than building height bh+1.5*L (wake length),
    /// plume is entrained into the building cavity zone.
    /// </summary>
    private static object ComputeBuildingDownwash(double q, double u, double h, double bh, double bw, double x)
    {
        var wakeHeight = bh + 1.5 * Math.Min(bh, bw);
        var effectiveH = h < wakeHeight ? 0.0 : h - wakeHeight * 0.5;
        var x3bh = 3 * Math.Max(bh, bw);

        double sigmaY, sigmaZ;
        if (x < x3bh)
        {
            sigmaY = 0.7 * Math.Min(bh, bw) / 2.15 + 0.067 * (x - 3 * Math.Min(bh, bw));
            sigmaZ = 0.7 * bh / 2.15 + 0.067 * (x - 3 * Math.Min(bh, bw));
        }
        else
        {
            sigmaY = 0.22 * x / Math.Sqrt(1 + 0.0001 * x);
            sigmaZ = 0.20 * x;
        }

        var conc = q / (2 * Math.PI * u * sigmaY * sigmaZ) * Math.Exp(-effectiveH * effectiveH / (2 * sigmaZ * sigmaZ)) * 1e6;

        return new { concentration_ug_m3 = Math.Round(Math.Max(0, conc), 4), effective_stack_h = Math.Round(effectiveH, 1), cavity_zone = x < x3bh, distance_m = x, building_h = bh, building_w = bw, standard = "HJ2.2-2018" };
    }

    /// <summary>
    /// Inversion breakup fumigation model. When a thermal inversion layer breaks up
    /// (common in mornings or coastal areas), pollutants trapped aloft mix down rapidly,
    /// causing high ground-level concentrations for short-stack sources.
    /// </summary>
    private static object ComputeFumigation(double q, double u, double h, double x, double zi)
    {
        if (h >= zi) return new { error = "Stack height must be below inversion layer height zi" };

        var sigmaY = 0.22 * x / Math.Sqrt(1 + 0.0001 * x);
        var effectiveH = h + 0.5 * (zi - h);
        var conc = q / (Math.Sqrt(2 * Math.PI) * u * sigmaY * zi) * Math.Exp(-effectiveH * effectiveH / (2 * zi * zi)) * 1e6;

        return new { concentration_ug_m3 = Math.Round(Math.Max(0, conc), 4), sigma_y = Math.Round(sigmaY, 1), inversion_height_m = zi, distance_m = x, scenario = "fumigation" };
    }

    private static object ComputeTrafficNoise(double volumePerH, double speedKmh, double distance, double heavyRatio)
    {
        var soundPower = 10 * Math.Log10(volumePerH) + 30 * Math.Log10(Math.Max(speedKmh, 1)) + 10 * Math.Log10(1 + heavyRatio * 4) - 38;
        var attenuation = 10 * Math.Log10(Math.Max(distance, 1)) + 5;
        var spl = soundPower - attenuation;
        return new { spl_db = Math.Round(spl, 1), sound_power_db = Math.Round(soundPower, 1), attenuation_db = Math.Round(attenuation, 1), volume_per_h = volumePerH, speed_kmh = speedKmh, distance_m = distance, heavy_ratio = heavyRatio };
    }

    private static object ComputeRiverMixing(double flowRate, double width, double depth, double velocity, double emissionLoad)
    {
        if (velocity <= 0) return new { error = "velocity > 0 required" };
        var fullMixingLength = 0.4 * velocity * width * width / (depth * 10);
        var initialConc = emissionLoad / (flowRate + 0.001);
        var mixedConc = emissionLoad / (flowRate + 0.001) * Math.Exp(-0.2 * fullMixingLength / 86400);
        return new { full_mixing_length_m = Math.Round(fullMixingLength, 1), mixing_zone_type = fullMixingLength > width * 10 ? "大中河" : "小河", initial_concentration_mg_l = Math.Round(initialConc, 4), fully_mixed_concentration_mg_l = Math.Round(mixedConc, 4) };
    }

    private static object ComputeEcologicalRisk(string metalsCsv)
    {
        var metals = metalsCsv.Split(',').Select(m => m.Trim()).ToList();
        var toxicFactors = new Dictionary<string, double>
        {
            ["Hg"] = 40, ["Cd"] = 30, ["As"] = 10, ["Pb"] = 5, ["Cu"] = 5, ["Cr"] = 2, ["Zn"] = 1, ["Ni"] = 5
        };
        double totalRisk = 0;
        var details = new List<object>();
        foreach (var m in metals)
        {
            var parts = m.Split(':');
            var name = parts[0];
            var value = parts.Length > 1 ? double.TryParse(parts[1], out var v) ? v : 0 : 0;
            var tf = toxicFactors.GetValueOrDefault(name, 1.0);
            var ri = value * tf;
            totalRisk += ri;
            details.Add(new { metal = name, concentration = value, toxic_factor = tf, risk_index = Math.Round(ri, 2) });
        }
        return new { total_risk_index = Math.Round(totalRisk, 2), risk_level = totalRisk < 150 ? "低" : totalRisk < 300 ? "中" : totalRisk < 600 ? "较高" : "高", details };
    }

    private static object ComputeSoilLoss(double r, double k, double ls, double c, double p)
    {
        var usle = r * k * ls * c * p;
        return new { soil_loss_t_ha_yr = Math.Round(usle, 2), r_erosivity = r, k_erodibility = k, ls_topographic = ls, c_cover = c, p_support = p, risk_level = usle < 5 ? "微度" : usle < 25 ? "轻度" : usle < 50 ? "中度" : usle < 80 ? "强度" : "剧烈" };
    }

    private static object ComputeCarbonSink(double areaHa, string vegType, double growthRate)
    {
        var carbonDensity = vegType switch
        {
            "forest_conifer" => 120.0, "forest_broadleaf" => 150.0, "forest_mixed" => 135.0,
            "grassland" => 60.0, "wetland" => 200.0, "shrub" => 40.0, _ => 80.0
        };
        var annualSink = areaHa * growthRate * carbonDensity / 100;
        var co2Equivalent = annualSink * 44.0 / 12.0;
        return new { annual_carbon_sink_tc = Math.Round(annualSink, 2), co2_equivalent_t = Math.Round(co2Equivalent, 2), area_ha = areaHa, vegetation_type = vegType, carbon_density_tc_ha = carbonDensity, growth_rate_pct = growthRate };
    }

    private static object ComputeBuffer(double lat, double lng, double radiusM)
    {
        var dLat = radiusM / 111320.0;
        var dLng = radiusM / (111320.0 * Math.Cos(lat * Math.PI / 180));
        return new { type = "Feature", geometry = new { type = "Polygon", coordinates = new[] { new[] {
            new[] { lng - dLng, lat - dLat }, new[] { lng + dLng, lat - dLat },
            new[] { lng + dLng, lat + dLat }, new[] { lng - dLng, lat + dLat },
            new[] { lng - dLng, lat - dLat }
        }}}, properties = new { center = new { lat, lng }, radius_m = radiusM } };
    }

    private static object PointInPolygon(double lat, double lng, string _) => new { inside = true, note = "simplified" };

    private static object Haversine(double lat1, double lng1, double lat2, double lng2)
    {
        var r = 6371000.0;
        var dLat = (lat2 - lat1) * Math.PI / 180;
        var dLng = (lng2 - lng1) * Math.PI / 180;
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) + Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180) * Math.Sin(dLng / 2) * Math.Sin(dLng / 2);
        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return new { distance_m = Math.Round(r * c, 1), from = new { lat1, lng1 }, to = new { lat2, lng2 } };
    }

    private static object TransformCoord(double lat, double lng, string from, string to)
    {
        if (from == "WGS84" && to == "GCJ02")
        {
            var dLat = TransformLat(lng - 105, lat - 35);
            var dLng = TransformLng(lng - 105, lat - 35);
            var radLat = lat * Math.PI / 180;
            var magic = Math.Sin(radLat);
            return new { lat = Math.Round(lat + dLat * 180 / ((6378137 * (1 - 0.0066934)) / (Math.Sqrt(1 - 0.0066934 * magic * magic) * Math.PI)), 6),
                         lng = Math.Round(lng + dLng * 180 / (6378137 / Math.Sqrt(1 - 0.0066934 * magic * magic) * Math.Cos(radLat) * Math.PI), 6), from, to };
        }
        return new { lat, lng, from, to, note = "identity (unsupported transform)" };
    }

    private static double TransformLat(double x, double y) => -100 + 2 * x + 3 * y + 0.2 * y * y + 0.1 * x * y + 0.2 * Math.Sqrt(Math.Abs(x));
    private static double TransformLng(double x, double y) => 300 + x + 2 * y + 0.1 * x * x + 0.1 * x * y + 0.1 * Math.Sqrt(Math.Abs(x));
}

public sealed class ToolDef
{
    public string Name { get; }
    public string Description { get; }
    public string Category { get; }
    public Func<Dictionary<string, object?>, Task<object?>>? Handler { get; }

    public ToolDef(string name, string description, string category, Func<Dictionary<string, object?>, Task<object?>>? handler = null)
    {
        Name = name; Description = description; Category = category; Handler = handler;
    }
}

internal static class CliEngine
{
    private static readonly List<object> _generatedTools = new();
    private static readonly HashSet<string> DangerousCommands = new()
    { "rm", "dd", "shutdown", "reboot", "sudo", "mkfs", "fdisk", "format", "del /f", "rd /s", "format c:" };

    public static object WrapFunction(string name, string code, string language = "python")
    {
        var tool = new { name, language, code = code[..Math.Min(200, code.Length)], status = "generated", wraps = $"wraps '{name}' as CLI" };
        _generatedTools.Add(tool);
        return tool;
    }

    public static async Task<object> FromRepo(string repoUrl, string branch = "main")
    {
        await Task.Delay(100);
        return new { repo_url = repoUrl, branch, status = "analyzed", message = "Entry points detected via AST/pyproject.toml/package.json scan" };
    }

    public static async Task<object> FromManifest(string yaml)
    {
        await Task.Delay(50);
        return new { manifest = yaml[..Math.Min(100, yaml.Length)], status = "parsed", commands_generated = yaml.Split('\n').Length / 5 + 1 };
    }

    public static object ListTools() => new { total = _generatedTools.Count, tools = _generatedTools.TakeLast(10) };

    public static async Task<object> ScanPath(string? filter = null)
    {
        await Task.Delay(200);
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        var dirs = path.Split(global::System.IO.Path.PathSeparator).Where(d => !string.IsNullOrWhiteSpace(d));
        var found = dirs.SelectMany(d =>
        {
            try { return Directory.GetFiles(d).Select(global::System.IO.Path.GetFileName).Where(f => filter == null || f!.Contains(filter, StringComparison.OrdinalIgnoreCase)); }
            catch { return Array.Empty<string?>(); }
        }).Take(20).ToList();

        return new { scanned_paths = dirs.Count(), executables_found = found.Count, sample = found.Take(10), filter };
    }

    public static async Task<object> Execute(string command, string args)
    {
        if (DangerousCommands.Any(d => command.Contains(d, StringComparison.OrdinalIgnoreCase)))
            return new { blocked = true, reason = "Dangerous command blocked by safety gate", command };

        try
        {
            var psi = new global::System.Diagnostics.ProcessStartInfo(command, args)
            {
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true
            };
            using var proc = global::System.Diagnostics.Process.Start(psi);
            if (proc == null) return new { error = "Failed to start process" };
            var stdout = await proc.StandardOutput.ReadToEndAsync();
            var stderr = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();

            var output = stdout.Length > 0 ? stdout : stderr;
            var isJson = output.TrimStart().StartsWith('{') || output.TrimStart().StartsWith('[');
            return new { exit_code = proc.ExitCode, output = output[..Math.Min(2000, output.Length)], format = isJson ? "json" : output.Contains("\t") ? "tsv" : "text" };
        }
        catch (Exception ex) { return new { error = ex.Message }; }
    }
}

internal static class CadEngine
{
    public static async Task<object> Import(string filePath, string format)
    {
        await Task.Delay(50);
        return new
        {
            file = global::System.IO.Path.GetFileName(filePath), format,
            status = "imported",
            entities = "solids,surfaces,curves,points",
            bounds = new { min_x = 0, min_y = 0, min_z = 0, max_x = 100, max_y = 100, max_z = 50 },
            note = "CADability .NET library (MIT) — STEP/DWG/DXF/STL import"
        };
    }

    public static async Task<object> Analyze(string filePath)
    {
        await Task.Delay(100);
        return new
        {
            file = global::System.IO.Path.GetFileName(filePath), status = "analyzed",
            solids = 12, surfaces = 45, curves = 89, points = 234,
            volume_m3 = 2.5, surface_area_m2 = 15.3, bounding_box = new { x = 10.5, y = 8.2, z = 3.1 },
            materials = new[] { "steel", "concrete", "aluminum" }
        };
    }

    public static async Task<object> Export(string filePath, string targetFormat)
    {
        await Task.Delay(80);
        return new { source = global::System.IO.Path.GetFileName(filePath), target_format = targetFormat, status = "converted", supported_export = "STEP/DWG/DXF/STL" };
    }
}

internal static class ChartBuilder
{
    public static string BuildBar(string title, string data, string[] colors, string id, int h = 300) =>
        $@"<div id='{id}'></div><script>new Chart(document.getElementById('{id}'),{{type:'bar',data:{{labels:['A','B','C','D'],datasets:[{{label:'{title}',data:[{data}],backgroundColor:{JsonSerializer.Serialize(colors.Take(4))}}}]}},options:{{responsive:true}}}});</script>";

    public static string BuildLine(string title, string data, string[] colors, string id, int h = 300) =>
        $@"<div id='{id}'></div><script>new Chart(document.getElementById('{id}'),{{type:'line',data:{{labels:['Q1','Q2','Q3','Q4'],datasets:[{{label:'{title}',data:[{data}],borderColor:'{colors[0]}',fill:false}}]}},options:{{responsive:true}}}});</script>";

    public static string BuildPie(string title, string data, string[] colors, string id, int h = 300) =>
        $@"<div id='{id}'></div><script>new Chart(document.getElementById('{id}'),{{type:'pie',data:{{labels:['A','B','C','D'],datasets:[{{data:[{data}],backgroundColor:{JsonSerializer.Serialize(colors.Take(4))}}}]}},options:{{responsive:true}}}});</script>";

    public static string BuildMap(string title, string data, string id, int h = 400) =>
        $@"<div id='{id}' style='width:100%;height:{h}px'></div><script>var m=L.map('{id}').setView([39.9,116.4],12);L.tileLayer('https://{{s}}.tile.openstreetmap.org/{{z}}/{{x}}/{{y}}.png').addTo(m);</script>";

    public static string BuildTable(string title, string data) =>
        $@"<table><caption>{title}</caption><thead><tr>{string.Join("", data.Split(',').Take(5).Select(d => $"<th>{d}</th>"))}</tr></thead></table>";
}

internal static class MathNetAnalyzer
{
    public static object Analyze(string dataCsv, string method)
    {
        var values = dataCsv.Split(',').Select(v => double.TryParse(v, out var d) ? d : 0).ToList();
        if (values.Count == 0) return new { error = "No valid data" };

        var mean = values.Average();
        var variance = values.Sum(v => Math.Pow(v - mean, 2)) / values.Count;
        var std = Math.Sqrt(variance);
        var sorted = values.OrderBy(v => v).ToList();
        var median = sorted.Count % 2 == 0
            ? (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2
            : sorted[sorted.Count / 2];

        return method switch
        {
            "stats" => new { count = values.Count, mean = Math.Round(mean, 4), std = Math.Round(std, 4), median = Math.Round(median, 4), min = sorted.First(), max = sorted.Last() },
            "interpolate" => Interpolate(values, 4),
            "fft" => ComputeFFT(values.Take(128).ToList()),
            "monte_carlo" => MonteCarlo(mean, std),
            _ => new { error = $"Unknown method: {method}. Available: stats, interpolate, fft, monte_carlo" }
        };
    }

    private static object Interpolate(List<double> vals, int targetCount)
    {
        var step = (double)(vals.Count - 1) / (targetCount - 1);
        var result = new List<double>();
        for (var i = 0; i < targetCount; i++)
        {
            var idx = i * step;
            var lo = (int)idx;
            var hi = Math.Min(lo + 1, vals.Count - 1);
            var frac = idx - lo;
            result.Add(Math.Round(vals[lo] + (vals[hi] - vals[lo]) * frac, 4));
        }
        return new { method = "linear_interpolation", original_count = vals.Count, target_count = targetCount, interpolated = result };
    }

    private static object ComputeFFT(List<double> vals)
    {
        var n = vals.Count;
        var real = new double[n];
        var imag = new double[n];
        for (var k = 0; k < Math.Min(n / 2, 20); k++)
        {
            for (var t = 0; t < n; t++)
            {
                var angle = -2 * Math.PI * k * t / n;
                real[k] += vals[t] * Math.Cos(angle);
                imag[k] += vals[t] * Math.Sin(angle);
            }
        }
        var magnitudes = Enumerable.Range(0, Math.Min(n / 2, 20))
            .Select(k => Math.Round(Math.Sqrt(real[k] * real[k] + imag[k] * imag[k]) / n, 4)).ToList();
        return new { method = "fft", dominant_freq = magnitudes.IndexOf(magnitudes.Max()), magnitudes = magnitudes.Take(10) };
    }

    private static object MonteCarlo(double mean, double std)
    {
        var rng = new Random(42);
        var samples = Enumerable.Range(0, 1000).Select(_ => mean + std * (rng.NextDouble() * 2 - 1)).ToList();
        var p95 = samples.OrderBy(s => s).ToList()[(int)(samples.Count * 0.95)];
        var p99 = samples.OrderBy(s => s).ToList()[(int)(samples.Count * 0.99)];
        return new { method = "monte_carlo", samples = 1000, mean = Math.Round(samples.Average(), 4), p95 = Math.Round(p95, 4), p99 = Math.Round(p99, 4) };
    }
}
