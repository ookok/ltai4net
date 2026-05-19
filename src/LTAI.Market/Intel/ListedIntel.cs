using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Market.Intel;

public sealed class ListedCompanyIntel
{
    private static readonly Lazy<ListedCompanyIntel> _instance = new(() => new ListedCompanyIntel());
    public static ListedCompanyIntel Instance => _instance.Value;

    private readonly ILogger<ListedCompanyIntel> _logger;
    private readonly Dictionary<string, ListedCompany> _companies;

    public ListedCompanyIntel(ILogger<ListedCompanyIntel>? logger = null)
    {
        _logger = logger ?? NullLogger<ListedCompanyIntel>.Instance;

        _companies = new Dictionary<string, ListedCompany>(StringComparer.OrdinalIgnoreCase)
        {
            ["龙净环保"] = new("龙净环保", "龙净", "600388", "SH", "环保", "大气治理", "中盘", new[] { "除尘", "脱硫", "脱硝", "烟气治理" }),
            ["菲达环保"] = new("菲达环保", "菲达", "600526", "SH", "环保", "大气治理", "小盘", new[] { "除尘", "烟气治理", "电除尘" }),
            ["雪迪龙"] = new("雪迪龙", "雪迪龙", "002658", "SZ", "环保", "环境监测", "小盘", new[] { "环境监测", "CEMS", "VOC监测" }),
            ["先河环保"] = new("先河环保", "先河", "300137", "SZ", "环保", "环境监测", "小盘", new[] { "网格化监测", "大气监测", "水质监测" }),
            ["聚光科技"] = new("聚光科技", "聚光", "300203", "SZ", "环保", "环境监测", "中盘", new[] { "环境监测", "实验室分析", "工业过程分析" }),
            ["华测检测"] = new("华测检测", "华测", "300012", "SZ", "检测", "第三方检测", "大盘", new[] { "环境检测", "食品检测", "计量校准" }),
            ["广电计量"] = new("广电计量", "广电计量", "002967", "SZ", "检测", "计量检测", "小盘", new[] { "计量", "检测", "认证" }),
            ["国检集团"] = new("国检集团", "国检", "603060", "SH", "检测", "检验认证", "小盘", new[] { "检验", "认证", "检测", "建材" }),
            ["苏交科"] = new("苏交科", "苏交科", "300284", "SZ", "工程咨询", "交通设计", "小盘", new[] { "交通设计", "勘察设计", "检测" }),
            ["中设集团"] = new("中设集团", "中设", "603018", "SH", "工程咨询", "勘察设计", "小盘", new[] { "勘察设计", "城市规划", "交通规划" }),
            ["碧水源"] = new("碧水源", "碧水源", "300070", "SZ", "环保", "水处理", "小盘", new[] { "膜技术", "污水处理", "水环境治理" }),
            ["万邦达"] = new("万邦达", "万邦达", "300055", "SZ", "环保", "水处理", "小盘", new[] { "工业水处理", "废水处理", "再生水" }),
            ["清新环境"] = new("清新环境", "清新", "002573", "SZ", "环保", "大气治理", "小盘", new[] { "脱硫", "脱硝", "除尘", "烟气治理" }),
            ["永清环保"] = new("永清环保", "永清", "300187", "SZ", "环保", "土壤修复", "小盘", new[] { "土壤修复", "固废处理", "烟气治理" }),
            ["金风科技"] = new("金风科技", "金风", "002202", "SZ", "新能源", "风电", "大盘", new[] { "风电", "风机制造", "风电场运营" }),
            ["明阳智能"] = new("明阳智能", "明阳", "601615", "SH", "新能源", "风电", "中盘", new[] { "风电", "风机", "新能源" })
        };
    }

    public List<EconomicSignal> Detect(List<Dictionary<string, object?>> announcements)
    {
        var signals = new List<EconomicSignal>();

        foreach (var ann in announcements)
        {
            var title = GetString(ann, "title", "");
            var date = GetString(ann, "ann_date", GetString(ann, "date", DateTime.Now.ToString("yyyy-MM-dd")));
            var stage = GetString(ann, "stage", "");
            var sourceUrl = GetString(ann, "source_url", "");
            var sourceUrlOrNull = string.IsNullOrWhiteSpace(sourceUrl) ? null : sourceUrl;

            var company = MatchCompany(title);
            if (company == null)
                continue;

            var (signalType, impact, inference) = InferSignalType(title, stage);

            var daysAgo = DateTime.TryParse(date, out var parsedDate)
                ? (float)(DateTime.Now - parsedDate).TotalDays
                : 0f;
            var timeDecayFactor = (float)Math.Exp(-0.05 * daysAgo);

            var signal = new EconomicSignal(
                Id: Guid.NewGuid().ToString("N")[..12],
                Company: company,
                AnnTitle: title,
                AnnDate: date,
                SignalType: signalType,
                Confidence: 0.7f,
                Inference: inference,
                EstimatedImpact: impact,
                TimeDecayFactor: (float)Math.Round(timeDecayFactor, 3),
                SourceUrl: sourceUrlOrNull
            );

            signals.Add(signal);
        }

        return signals;
    }

    public string GenerateReport(List<EconomicSignal> signals)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# 上市公司经济信号监测报告");
        sb.AppendLine();
        sb.AppendLine($"生成时间: {DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine($"信号总数: {signals.Count}");
        sb.AppendLine();

        var grouped = signals
            .GroupBy(s => s.Company.StockCode)
            .OrderBy(g => g.Key);

        foreach (var group in grouped)
        {
            var company = group.First().Company;
            sb.AppendLine($"## {company.StockCode} {company.ShortName} ({company.Name})");
            sb.AppendLine($"- 交易所: {company.Exchange}");
            sb.AppendLine($"- 行业: {company.Industry} > {company.SubIndustry}");
            sb.AppendLine($"- 市值分类: {company.MarketCapCategory}");
            sb.AppendLine();

            foreach (var signal in group.OrderByDescending(s => s.TimeDecayFactor))
            {
                var impactIcon = signal.EstimatedImpact switch
                {
                    "正面" => "",
                    "负面" => "",
                    _ => "➖"
                };

                sb.AppendLine($"### {impactIcon} [{signal.SignalType}] {signal.AnnTitle}");
                sb.AppendLine($"- 日期: {signal.AnnDate}");
                sb.AppendLine($"- 信号类型: {signal.SignalType}");
                sb.AppendLine($"- 置信度: {signal.Confidence:P0}");
                sb.AppendLine($"- 影响判断: {signal.EstimatedImpact}");
                sb.AppendLine($"- 推理: {signal.Inference}");
                sb.AppendLine($"- 时间衰减因子: {signal.TimeDecayFactor:F3}");

                if (signal.SourceUrl != null)
                    sb.AppendLine($"- 来源: {signal.SourceUrl}");

                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    public Dictionary<string, object> GetPeerActivity(string stockCode)
    {
        var company = _companies.Values.FirstOrDefault(c =>
            c.StockCode.Equals(stockCode, StringComparison.OrdinalIgnoreCase));

        if (company == null)
            return new Dictionary<string, object> { ["error"] = $"Stock code {stockCode} not found" };

        var peers = _companies.Values
            .Where(c => c.Industry == company.Industry && c.StockCode != company.StockCode)
            .Select(c => new
            {
                c.Name,
                c.ShortName,
                c.StockCode,
                c.Exchange,
                c.SubIndustry
            })
            .ToList();

        return new Dictionary<string, object>
        {
            ["company"] = new { company.Name, company.StockCode, company.Industry },
            ["peers"] = peers,
            ["peer_count"] = peers.Count
        };
    }

    public ListedCompany? MatchCompany(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return null;

        foreach (var (name, company) in _companies)
        {
            if (title.Contains(name))
                return company;
        }

        foreach (var (_, company) in _companies)
        {
            if (title.Contains(company.ShortName))
                return company;
        }

        foreach (var (_, company) in _companies)
        {
            foreach (var keyword in company.Keywords)
            {
                if (title.Contains(keyword))
                {
                    var companyNameInTitle = _companies.Keys.FirstOrDefault(n => title.Contains(n));
                    if (companyNameInTitle != null)
                        continue;
                }
            }
        }

        return null;
    }

    public static (string SignalType, string Impact, string Inference) InferSignalType(string title, string stage)
    {
        if (stage.Contains("受理公示") || stage.Contains("审批"))
        {
            if (title.Contains("扩建") || title.Contains("新建") || title.Contains("扩产"))
                return ("expansion", "正面", "公示新项目受理审批，暗示公司业务扩张");

            if (title.Contains("技术改造") || title.Contains("升级"))
                return ("expansion", "正面", "技术改造类公示，公司加大技术投入");

            return ("compliance", "中性", "行政审批公示，正常业务流程");
        }

        if (stage.Contains("招标公告") || stage.Contains("招标"))
            return ("pipeline", "正面", "招标公告表明市场新需求/项目启动");

        if (stage.Contains("中标") || stage.Contains("中标公告"))
            return ("pipeline", "正面", "中标公告确认公司获得项目，提升营收预期");

        if (stage.Contains("验收") || stage.Contains("验收公示"))
            return ("capacity", "正面", "项目验收通过，表明公司产能交付完成");

        if (title.Contains("处罚") || title.Contains("违规") || title.Contains("停产"))
            return ("risk", "负面", "公示涉及处罚/违规，公司面临合规风险");

        if (title.Contains("停产") || title.Contains("限产"))
            return ("risk", "负面", "限停产通告，影响公司产能");

        return ("compliance", "中性", "其他公告信息，与业务运营相关");
    }

    private static string GetString(Dictionary<string, object?> data, string key, string defaultValue)
    {
        if (data.TryGetValue(key, out var val) && val != null)
            return val.ToString() ?? defaultValue;
        return defaultValue;
    }
}
