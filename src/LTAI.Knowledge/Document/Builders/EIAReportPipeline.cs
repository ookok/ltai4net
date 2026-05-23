using LTAI.Core.Models;
using LTAI.Knowledge.Document;
using LTAI.Knowledge.Document.Models;
using Microsoft.Extensions.Logging;

namespace LTAI.Knowledge.Document;

public sealed class EIAReportPipeline
{
    private readonly EIAReportBuilder _builder = new();
    private readonly ILogger<EIAReportPipeline> _logger;

    public EIAReportPipeline(ILogger<EIAReportPipeline> logger)
    {
        _logger = logger;
    }

    public async Task<string> GenerateReportAsync(
        Dictionary<string, object> inputs,
        string outputDir,
        CancellationToken ct = default)
    {
        _logger.LogInformation("EIAReportPipeline: Generating report for {InputCount} inputs", inputs.Count);

        // Step 1: Run all EIA model calculations
        var quantitativeResults = EIAEngine.RunAll(inputs);
        _logger.LogInformation("EIAReportPipeline: Calculated {Count} quantitative results", quantitativeResults.Count);

        // Step 2: Build compliance context
        var complianceContext = BuildComplianceContext(inputs);

        // Step 3: Validate against regulation database
        var validationResults = ValidateRegulationCompliance(inputs, quantitativeResults);

        // Step 4: Build report document
        var report = BuildReportDocument(inputs, quantitativeResults, complianceContext, validationResults);

        // Step 5: Generate DOCX
        var outputPath = Path.Combine(outputDir, $"EIA_Report_{DateTime.Now:yyyyMMdd_HHmmss}.docx");
        Directory.CreateDirectory(outputDir);
        _builder.Build(report, outputPath);

        _logger.LogInformation("EIAReportPipeline: Report saved to {Path}", outputPath);
        return outputPath;
    }

    private static string BuildComplianceContext(Dictionary<string, object> inputs)
    {
        var category = inputs.TryGetValue("category", out var cat) ? cat?.ToString() ?? "大气" : "大气";
        return EiaRegulationAnchor.BuildContextPrompt(category);
    }

    private static Dictionary<string, object> ValidateRegulationCompliance(
        Dictionary<string, object> inputs,
        Dictionary<string, double> results)
    {
        var validation = new Dictionary<string, object>();

        // Air quality check
        if (results.TryGetValue("plume", out var plume))
        {
            var standard = EiaRegulationAnchor.Search("GB 3095");
            validation["air_quality"] = new
            {
                concentration = plume,
                unit = "mg/m³",
                standard = standard.FirstOrDefault()?.Id ?? "GB 3095-2012",
                passes_limit = plume < 0.5 // Conservative threshold
            };
        }

        // Water quality check
        if (results.TryGetValue("do", out var do_))
        {
            validation["water_quality"] = new
            {
                dissolved_oxygen = do_,
                unit = "mg/L",
                standard = "GB 3838-2002",
                passes_limit = do_ >= 5.0
            };
        }

        // Noise check
        if (results.TryGetValue("noise", out var noise))
        {
            validation["noise"] = new
            {
                level = noise,
                unit = "dB(A)",
                standard = "GB 3096-2008",
                passes_limit = noise < 70
            };
        }

        // GHG check
        if (results.TryGetValue("co2e", out var co2e))
        {
            var scopeClass = EIAEngine.CarbonGHG.ScopeClassify(inputs.TryGetValue("source_type", out var st) ? st?.ToString() ?? "" : "combustion");
            validation["carbon"] = new
            {
                co2e_tons = co2e,
                scope = scopeClass
            };
        }

        return validation;
    }

    private static ReportDocument BuildReportDocument(
        Dictionary<string, object> inputs,
        Dictionary<string, double> results,
        string complianceContext,
        Dictionary<string, object> validation)
    {
        var report = new ReportDocument
        {
            Sections = new List<ReportSection>(),
            Tables = new List<TableSection>()
        };

        // Cover
        report.Sections.Add(new ReportSection
        {
            Text = "环境影响评价报告",
            Type = "Heading",
            Style = new StyleDef { Align = "center", Size = 22, Bold = true }
        });
        report.Sections.Add(new ReportSection
        {
            Text = $"生成日期: {DateTime.Now:yyyy年MM月dd日}",
            Type = "Paragraph",
            Style = new StyleDef { Align = "center", Size = 12 }
        });

        // Standards reference
        report.Sections.Add(new ReportSection
        {
            Text = "一、评价标准与依据",
            Type = "Heading1",
            Style = new StyleDef { Bold = true, Size = 16 },
            PageBreakBefore = true
        });
        report.Sections.Add(new ReportSection
        {
            Text = complianceContext,
            Type = "Paragraph",
            Style = new StyleDef { Indent = "firstLine2Char", Size = 12 }
        });

        // Quantitative results
        report.Sections.Add(new ReportSection
        {
            Text = "二、定量预测结果",
            Type = "Heading1",
            Style = new StyleDef { Bold = true, Size = 16 }
        });

        foreach (var (key, value) in results)
        {
            var label = key switch
            {
                "plume" => "大气污染物浓度",
                "do" => "溶解氧浓度",
                "noise" => "噪声级",
                "co2e" => "二氧化碳当量",
                "npv" => "社会经济净现值",
                _ => key
            };
            report.Sections.Add(new ReportSection
            {
                Text = $"{label}: {value:F4}",
                Type = "Paragraph",
                Style = new StyleDef { Indent = "firstLine2Char", Size = 12 }
            });
        }

        // Validation results as table
        report.Sections.Add(new ReportSection
        {
            Text = "三、法规符合性验证",
            Type = "Heading1",
            Style = new StyleDef { Bold = true, Size = 16 }
        });

        var validationTable = new TableSection
        {
            Caption = "合规性检查结果",
            Headers = new List<string> { "检查项", "数值", "标准号", "是否达标" }
        };

        foreach (var (key, value) in validation)
        {
            if (value is System.Text.Json.JsonElement je)
            {
                var val = je.TryGetProperty("concentration", out var c) ? c.ToString() :
                         je.TryGetProperty("dissolved_oxygen", out var d) ? d.ToString() :
                         je.TryGetProperty("level", out var l) ? l.ToString() :
                         je.TryGetProperty("co2e_tons", out var co) ? co.ToString() : "-";
                var std = je.TryGetProperty("standard", out var s) ? s.GetString() ?? "-" : "-";
                var pass = je.TryGetProperty("passes_limit", out var p) && p.GetBoolean();

                validationTable.Rows.Add(new List<string> { key, val, std, pass ? "是 ✓" : "否 ⚠️" });
            }
        }

        // If no table rows, add sample data
        if (validationTable.Rows.Count == 0)
        {
            foreach (var (key, value) in results.Take(4))
            {
                validationTable.Rows.Add(new List<string> { key, $"{value:F4}", "参照适用标准", "待确认" });
            }
        }

        report.Tables.Add(validationTable);

        // Conclusions
        report.Sections.Add(new ReportSection
        {
            Text = "四、结论与建议",
            Type = "Heading1",
            Style = new StyleDef { Bold = true, Size = 16 }
        });
        report.Sections.Add(new ReportSection
        {
            Text = "本报告基于定量模型计算，参照现行中国环境标准。预测结果需结合现场监测数据进行验证。",
            Type = "Paragraph",
            Style = new StyleDef { Indent = "firstLine2Char", Size = 12 }
        });

        return report;
    }
}
