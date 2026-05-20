using System.Text.Json;
using LTAI.Vector.Knowledge.Models;

namespace LTAI.Metrics.Evaluation;

public enum GoldenQueryBucket
{
    ExactTerms,
    MultiHop,
    LongTail,
    Unanswerable,
    PermissionFilter
}

public sealed record GoldenQuery
{
    public string QueryId { get; init; } = "";
    public string QueryText { get; init; } = "";
    public GoldenQueryBucket Bucket { get; init; } = GoldenQueryBucket.ExactTerms;
    public List<string> RelevantDocIds { get; init; } = new();
    public List<string> ExpectedAnswerFragments { get; init; } = new();
    public string ExpectedRejectionReason { get; init; } = "";
    public string Domain { get; init; } = "general";
    public double Priority { get; set; } = 1.0;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastVerifiedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed record BucketStats
{
    public GoldenQueryBucket Bucket { get; init; }
    public int QueryCount { get; init; }
    public double AvgPriority { get; init; }
    public DateTimeOffset OldestQuery { get; init; }
    public DateTimeOffset NewestQuery { get; init; }
}

public sealed record GoldenQueryReport
{
    public List<GoldenQuery> Queries { get; init; } = new();
    public Dictionary<GoldenQueryBucket, BucketStats> BucketStats { get; init; } = new();
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed class GoldenQueryManager
{
    private readonly Dictionary<string, GoldenQuery> _queries = new();
    private readonly object _lock = new();
    private readonly string _storagePath;

    public GoldenQueryManager(string storagePath = ".livingtree/eval/golden_queries")
    {
        _storagePath = storagePath;
        Directory.CreateDirectory(storagePath);
        LoadFromDisk();
    }

    public void AddQuery(GoldenQuery query)
    {
        lock (_lock)
        {
            _queries[query.QueryId] = query;
        }
    }

    public void AddQueries(IEnumerable<GoldenQuery> queries)
    {
        lock (_lock)
        {
            foreach (var q in queries)
                _queries[q.QueryId] = q;
        }
    }

    public bool RemoveQuery(string queryId)
    {
        lock (_lock)
        {
            return _queries.Remove(queryId);
        }
    }

    public GoldenQuery? GetQuery(string queryId)
    {
        lock (_lock)
        {
            return _queries.TryGetValue(queryId, out var q) ? q : null;
        }
    }

    public List<GoldenQuery> GetQueriesByBucket(GoldenQueryBucket bucket)
    {
        lock (_lock)
        {
            return _queries.Values
                .Where(q => q.Bucket == bucket)
                .OrderByDescending(q => q.Priority)
                .ToList();
        }
    }

    public List<GoldenQuery> GetHighPriorityQueries(double minPriority = 0.8)
    {
        lock (_lock)
        {
            return _queries.Values
                .Where(q => q.Priority >= minPriority)
                .OrderByDescending(q => q.Priority)
                .ToList();
        }
    }

    public List<GoldenQuery> GetAllQueries()
    {
        lock (_lock)
        {
            return _queries.Values.ToList();
        }
    }

    public List<(string query, List<string> relevantDocIds, string bucket)> BuildEvaluationSet(
        GoldenQueryBucket? bucket = null)
    {
        lock (_lock)
        {
            var filtered = bucket.HasValue
                ? _queries.Values.Where(q => q.Bucket == bucket.Value)
                : _queries.Values.AsEnumerable();

            return filtered.Select(q => (
                q.QueryText,
                q.RelevantDocIds,
                q.Bucket.ToString()
            )).ToList();
        }
    }

    public GoldenQueryReport GenerateReport()
    {
        lock (_lock)
        {
            var bucketStats = _queries.Values
                .GroupBy(q => q.Bucket)
                .ToDictionary(
                    g => g.Key,
                    g => new BucketStats
                    {
                        Bucket = g.Key,
                        QueryCount = g.Count(),
                        AvgPriority = g.Average(q => q.Priority),
                        OldestQuery = g.Min(q => q.CreatedAt),
                        NewestQuery = g.Max(q => q.CreatedAt)
                    });

            return new GoldenQueryReport
            {
                Queries = _queries.Values.ToList(),
                BucketStats = bucketStats
            };
        }
    }

    public int VerifyRelevance(string queryId, List<KnowledgeSearchResult> results)
    {
        lock (_lock)
        {
            if (!_queries.TryGetValue(queryId, out var gold))
                return -1;

            var retrievedIds = results.Select(r => r.Id).ToHashSet();
            var found = gold.RelevantDocIds.Count(id => retrievedIds.Contains(id));

            if (found == 0)
            {
                gold.Priority = Math.Min(1.0, gold.Priority + 0.1);
            }

            gold.LastVerifiedAt = DateTimeOffset.UtcNow;

            return found;
        }
    }

    public void MarkStale(string queryId)
    {
        lock (_lock)
        {
            if (_queries.TryGetValue(queryId, out var gold))
            {
                gold.Priority = Math.Max(0.1, gold.Priority - 0.05);
            }
        }
    }

    public async Task SaveToDiskAsync()
    {
        lock (_lock)
        {
            var json = JsonSerializer.Serialize(_queries.Values.ToList(), new JsonSerializerOptions
            {
                WriteIndented = true
            });

            var filePath = Path.Combine(_storagePath, "golden_queries.json");
            File.WriteAllText(filePath, json);
        }
    }

    public void SaveToDisk()
    {
        lock (_lock)
        {
            var json = JsonSerializer.Serialize(_queries.Values.ToList(), new JsonSerializerOptions
            {
                WriteIndented = true
            });

            var filePath = Path.Combine(_storagePath, "golden_queries.json");
            File.WriteAllText(filePath, json);
        }
    }

    private void LoadFromDisk()
    {
        var filePath = Path.Combine(_storagePath, "golden_queries.json");
        if (!File.Exists(filePath)) return;

        try
        {
            var json = File.ReadAllText(filePath);
            var loaded = JsonSerializer.Deserialize<List<GoldenQuery>>(json);
            if (loaded != null)
            {
                foreach (var q in loaded)
                    _queries[q.QueryId] = q;
            }
        }
        catch { }
    }

    public void SeedDefaults()
    {
        var defaults = new List<GoldenQuery>
        {
            new()
            {
                QueryId = "exact_model_xr2048",
                QueryText = "XR-2048 的功耗参数是多少",
                Bucket = GoldenQueryBucket.ExactTerms,
                RelevantDocIds = new() { "doc_xr2048_spec" },
                ExpectedAnswerFragments = new() { "功耗", "XR-2048", "瓦特" },
                Domain = "hardware",
                Priority = 1.0
            },
            new()
            {
                QueryId = "exact_api_timeout",
                QueryText = "配置 Nginx 超时参数 upstream_timeout",
                Bucket = GoldenQueryBucket.ExactTerms,
                RelevantDocIds = new() { "doc_nginx_config" },
                ExpectedAnswerFragments = new() { "proxy_read_timeout", "upstream" },
                Domain = "infrastructure",
                Priority = 1.0
            },
            new()
            {
                QueryId = "multihop_auth_flow",
                QueryText = "用户登录后如何获取 API Token 并用它调用数据接口",
                Bucket = GoldenQueryBucket.MultiHop,
                RelevantDocIds = new() { "doc_auth_login", "doc_api_token", "doc_data_api" },
                ExpectedAnswerFragments = new() { "登录", "token", "API" },
                Domain = "api",
                Priority = 0.9
            },
            new()
            {
                QueryId = "multihop_deploy_chain",
                QueryText = "从代码提交到生产环境部署的完整流程",
                Bucket = GoldenQueryBucket.MultiHop,
                RelevantDocIds = new() { "doc_git_workflow", "doc_ci_cd", "doc_deploy_k8s" },
                ExpectedAnswerFragments = new() { "提交", "构建", "部署" },
                Domain = "devops",
                Priority = 0.9
            },
            new()
            {
                QueryId = "longtail_legacy_migration",
                QueryText = "从 Python 2.7 项目的旧 ORM 迁移到 SQLAlchemy 2.0 的注意事项",
                Bucket = GoldenQueryBucket.LongTail,
                RelevantDocIds = new() { "doc_sqlalchemy_migration" },
                ExpectedAnswerFragments = new() { "SQLAlchemy", "迁移" },
                Domain = "development",
                Priority = 0.7
            },
            new()
            {
                QueryId = "longtail_obscure_error",
                QueryText = "遇到错误代码 ERR_CONNECTION_REFUSED 0x7F3A 怎么解决",
                Bucket = GoldenQueryBucket.LongTail,
                RelevantDocIds = new() { "doc_error_codes", "doc_network_troubleshoot" },
                ExpectedAnswerFragments = new() { "ERR_CONNECTION_REFUSED" },
                Domain = "operations",
                Priority = 0.7
            },
            new()
            {
                QueryId = "unanswerable_future_roadmap",
                QueryText = "下一个大版本的详细发布时间和功能列表",
                Bucket = GoldenQueryBucket.Unanswerable,
                RelevantDocIds = new(),
                ExpectedRejectionReason = "尚未公开发布计划",
                Domain = "general",
                Priority = 0.8
            },
            new()
            {
                QueryId = "unanswerable_personal_data",
                QueryText = "张三的个人工资和绩效评估详情",
                Bucket = GoldenQueryBucket.Unanswerable,
                RelevantDocIds = new(),
                ExpectedRejectionReason = "涉及个人隐私",
                Domain = "hr",
                Priority = 0.8
            },
            new()
            {
                QueryId = "permission_admin_only",
                QueryText = "数据库管理员账户的密码和连接方式",
                Bucket = GoldenQueryBucket.PermissionFilter,
                RelevantDocIds = new() { "doc_db_admin_guide" },
                ExpectedAnswerFragments = new() { "权限不足" },
                Domain = "security",
                Priority = 0.9
            },
            new()
            {
                QueryId = "permission_confidential_doc",
                QueryText = "财务部门年度审计报告的详细内容",
                Bucket = GoldenQueryBucket.PermissionFilter,
                RelevantDocIds = new() { "doc_finance_audit" },
                ExpectedAnswerFragments = new() { "权限不足" },
                Domain = "finance",
                Priority = 0.9
            }
        };

        AddQueries(defaults);
    }
}
