using LTAI.Planning.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Planning.Quality;

public sealed class Clarifier
{
    private readonly List<Clarification> _history = new();
    private readonly Lock _historyLock = new();
    private readonly ILogger _logger;

    private static readonly Lazy<Clarifier> _instance = new(() => new Clarifier());
    public static Clarifier Instance => _instance.Value;

    private Clarifier()
    {
        _logger = NullLoggerFactory.Instance.CreateLogger<Clarifier>();
    }

    public List<Clarification> Analyze(string userInput, string domain = "general", object? plan = null)
    {
        var questions = domain switch
        {
            "环评" => new[]
            {
                ("项目类型是什么？（如工业、交通、水利等）", new List<string> { "工业", "交通", "水利", "能源", "其他" }),
                ("项目所在区域是哪里？", new List<string> { "城区", "郊区", "农村", "生态保护区", "其他" }),
                ("主要污染物有哪些？", new List<string> { "废气", "废水", "噪声", "固废", "其他" }),
                ("适用什么评价标准？", new List<string> { "国家标准", "行业标准", "地方标准", "国际标准" }),
            },
            "应急" => new[]
            {
                ("应急事件类型是什么？", new List<string> { "火灾", "泄漏", "爆炸", "自然灾害", "其他" }),
                ("影响范围有多大？", new List<string> { "局部", "区域", "跨区域", "全国" }),
                ("可用应急资源有哪些？", new List<string> { "内部资源", "外部支援", "政府协调", "物资储备" }),
                ("时间紧迫程度如何？", new List<string> { "立即响应", "24小时内", "72小时内", "一周内" }),
            },
            "报告" => new[]
            {
                ("需要什么类型的报告？", new List<string> { "技术报告", "管理报告", "总结报告", "分析报告" }),
                ("报告的目标受众是谁？", new List<string> { "管理层", "技术人员", "客户", "监管机构" }),
                ("需要什么输出格式？", new List<string> { "PDF", "Word", "HTML", "Markdown" }),
                ("报告的截止时间是什么时候？", new List<string> { "今天", "本周", "本月", "下个月" }),
            },
            "code" => new[]
            {
                ("使用什么编程语言？", new List<string> { "C#", "Python", "JavaScript", "Java", "Go" }),
                ("使用什么框架或库？", new List<string> { ".NET", "React", "Vue", "Django", "无偏好" }),
                ("功能范围是什么？", new List<string> { "完整应用", "模块/组件", "脚本工具", "API接口" }),
            },
            _ => new[]
            {
                ("请详细说明您的目标或需求是什么？", new List<string> { "创建", "修改", "分析", "优化" }),
                ("有什么约束条件需要注意？", new List<string> { "时间", "资源", "技术", "合规" }),
                ("是否有特定的偏好或样式要求？", new List<string> { "简洁", "详细", "标准", "自定义" }),
            }
        };

        var results = new List<Clarification>(questions.Length);
        foreach (var (q, opts) in questions)
        {
            results.Add(CreateClarification(q, opts, ClarifierMode.FillBlank));
        }

        return results;
    }

    public void Record(Clarification clarification)
    {
        lock (_historyLock)
        {
            for (int i = 0; i < _history.Count; i++)
            {
                if (_history[i].Id == clarification.Id)
                {
                    _history[i].Answered = true;
                    _history[i].Answer = clarification.Answer;
                    return;
                }
            }
            clarification.Answered = true;
            _history.Add(clarification);
        }
    }

    public List<Clarification> GetAnswered()
    {
        lock (_historyLock)
        {
            return _history.Where(c => c.Answered).ToList();
        }
    }

    public Clarification CreateClarification(string question, List<string> options, ClarifierMode mode, string? defaultAnswer = null)
    {
        return new Clarification
        {
            Id = $"clf-{Guid.NewGuid():N}",
            Question = question,
            Options = options,
            Mode = mode,
            DefaultAnswer = defaultAnswer,
            Answered = false,
            Answer = null
        };
    }

    public Dictionary<string, object?> GetStats()
    {
        lock (_historyLock)
        {
            int pending = _history.Count(c => !c.Answered);
            int answered = _history.Count(c => c.Answered);
            return new Dictionary<string, object?>
            {
                ["pending"] = pending,
                ["answered"] = answered,
                ["total"] = _history.Count
            };
        }
    }
}
