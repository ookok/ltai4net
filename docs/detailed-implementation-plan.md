# LTAI 4 Net — 详细实施方案

本文档为架构审查计划（`review-architecture-plan.md`）的详细实现方案，包含核心数据结构、
接口定义、重构步骤和代码示例。

---

## Phase 0: 诊断工具（1 天）

### 目标

建立性能基线和决策数据。

### 诊断点

```csharp
// 新增：src/LTAI.Core/Diagnostics/DiagnosticsHostedService.cs
// 启动时采集：
// 1. Stopwatch per DI registration in AddLTAICore/AddLTAIAI/AddLTAIAgent
// 2. SQLite connection pool utilization
// 3. Cache hit/miss rates (all 8 caches)
// 4. Pipeline step latencies (histogram)
// 5. Agent call frequency (per-session heatmap)

// 输出到 .livingtree/diagnostics/{timestamp}.json
```

### DiagnosticsTelemetry.cs（新增）

```csharp
namespace LTAI.Core.Diagnostics;

public sealed class DiagnosticsTelemetry
{
    private readonly ConcurrentDictionary<string, DiagnosticBucket> _buckets = new();

    public IDisposable Measure(string category, string name)
    {
        var sw = Stopwatch.StartNew();
        return new DiagnosticScope(() =>
        {
            sw.Stop();
            var bucket = _buckets.GetOrAdd($"{category}:{name}", _ => new DiagnosticBucket());
            bucket.Record(sw.ElapsedTicks);
        });
    }

    public DiagnosticsSnapshot Snapshot() { /* ... */ }
}

internal sealed class DiagnosticBucket
{
    private long _min, _max, _sum, _count;
    // P50/P90/P99 via reservoir sampling (2048 samples)
    private readonly long[] _samples = new long[2048];
    private int _sampleIndex;

    public void Record(long ticks) { /* Interlocked operations */ }
}
```

---

## Phase 1: 核心简化（1-2 周）

### 1.1 合并 SQLite 数据库

#### 现状

| 文件 | 路径 | 用途 |
|------|------|------|
| `kg.db` | `.livingtree/` | KgStore + PalaceStore |
| `knowledge_graph.db` | `.livingtree/` | KbGraph 专用 |
| `memory.db` | `.livingtree/` | MemoryCachingStore |
| `circuit_breaker.db` | `.livingtree/` | CircuitBreakerStore |
| `sessions/*.session` | `.livingtree/sessions/` | SessionManager |
| `hpo/*.db` | `.livingtree/` | Hpo 实验 |

#### 目标

单文件 `.livingtree/ltai.db`，表前缀隔离。

#### 表空间设计

```sql
-- ============================================================
-- LTAI Unified Database Schema
-- ============================================================

-- PalaceStore (记忆宫殿)
CREATE TABLE palace_entries (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    wing TEXT NOT NULL,           -- coding, general, etc.
    room TEXT NOT NULL,           -- default, reflection, entity
    drawer_id TEXT,               -- scenario block id
    role TEXT NOT NULL,           -- user / assistant / system
    content TEXT NOT NULL,
    embedding BLOB,               -- TurboQuant 4-bit packed
    importance REAL DEFAULT 0.5,
    access_count INTEGER DEFAULT 0,
    agent_id TEXT,
    metadata TEXT,                -- JSON
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    expires_at TEXT
);

CREATE VIRTUAL TABLE palace_fts USING fts5(
    content, tokenize='unicode61'
);

-- KgStore (知识图谱)
CREATE TABLE kg_nodes (
    id TEXT PRIMARY KEY,
    kind TEXT NOT NULL,          -- class, method, file, concept
    name TEXT NOT NULL,
    description TEXT,
    embedding BLOB,
    metadata TEXT,
    created_at TEXT DEFAULT (datetime('now'))
);

CREATE TABLE kg_edges (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    source_id TEXT NOT NULL REFERENCES kg_nodes(id),
    target_id TEXT NOT NULL REFERENCES kg_nodes(id),
    relation TEXT NOT NULL,      -- calls, extends, defines, relates
    weight REAL DEFAULT 1.0,
    metadata TEXT,
    created_at TEXT DEFAULT (datetime('now'))
);

CREATE VIRTUAL TABLE kg_fts USING fts5(
    name, description, tokenize='unicode61'
);

-- CgGraph (代码图)
CREATE TABLE cg_symbols (    -- shares kg_nodes schema + extra fields
    id TEXT PRIMARY KEY,
    kind TEXT NOT NULL,
    name TEXT NOT NULL,
    file_path TEXT,
    line_start INTEGER,
    line_end INTEGER,
    namespace TEXT,
    signature TEXT,
    embedding BLOB,
    metadata TEXT,
    created_at TEXT DEFAULT (datetime('now'))
);

CREATE TABLE cg_edges (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    source_id TEXT NOT NULL REFERENCES cg_symbols(id),
    target_id TEXT NOT NULL REFERENCES cg_symbols(id),
    relation TEXT NOT NULL,      -- calls, inherits, implements, references
    weight REAL DEFAULT 1.0
);

-- Session (加密会话)
CREATE TABLE session_entries (
    id TEXT PRIMARY KEY,
    conversation_id TEXT NOT NULL,
    trace_id TEXT NOT NULL,
    user_id TEXT,
    encrypted_data BLOB NOT NULL,
    compression TEXT DEFAULT 'gzip',
    hash TEXT NOT NULL,          -- HMAC-SHA256 integrity
    message_count INTEGER DEFAULT 0,
    created_at TEXT DEFAULT (datetime('now')),
    updated_at TEXT DEFAULT (datetime('now'))
);
CREATE INDEX idx_sessions_conv ON session_entries(conversation_id);
CREATE INDEX idx_sessions_trace ON session_entries(trace_id);

-- 电路断路器
CREATE TABLE circuit_breaker (
    provider TEXT PRIMARY KEY,
    failures INTEGER NOT NULL DEFAULT 0,
    cooldown_until TEXT,
    last_failure_at TEXT
);

-- HPO 超参实验
CREATE TABLE hpo_trials (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    experiment_id TEXT NOT NULL,
    params TEXT NOT NULL,         -- JSON
    score REAL,
    duration_ms INTEGER,
    state TEXT DEFAULT 'pending', -- pending/running/completed/failed
    created_at TEXT DEFAULT (datetime('now'))
);

-- KgStore 质量分数
CREATE TABLE kg_quality_scores (
    node_id TEXT PRIMARY KEY,
    score REAL NOT NULL DEFAULT 1.0,
    updated_at TEXT DEFAULT (datetime('now'))
);

-- KgStore 版本追踪
CREATE TABLE kg_versions (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    version TEXT NOT NULL,
    snapshot TEXT NOT NULL,       -- JSON
    created_at TEXT DEFAULT (datetime('now'))
);
```

#### 迁移步骤

```
1. 新增 src/LTAI.Core/Storage/UnifiedDb.cs
    - 封装 SqliteConnection 管理
    - WAL 模式默认开启
    - 连接池: 2 reader + 1 writer (与当前模式兼容)
    - 自动迁移 (版本号模式)

2. 逐个迁移 Store:
    a. KgStore → UnifiedDb.KgStore （1 天）
    b. PalaceStore → UnifiedDb.Palace （1 天）
    c. SessionManager → UnifiedDb.Sessions （1 天）
    d. CircuitBreakerStore → UnifiedDb.CircuitBreaker （0.5 天）

3. 保留 IKgStore / IPalaceStore 接口不变

4. 旧文件标记为只读保留，7 天后删除
```

#### UnifiedDb 接口设计

```csharp
// src/LTAI.Core/Storage/UnifiedDb.cs
namespace LTAI.Core.Storage;

public sealed class UnifiedDb : IDisposable
{
    private readonly string _connectionString;
    private readonly SqliteConnection _writer;
    private readonly SqliteConnection _reader1;
    private readonly SqliteConnection _reader2;
    private int _schemaVersion;

    public UnifiedDb(string path)
    {
        _connectionString = $"Data Source={Path.Combine(path, "ltai.db")};Pooling=True;";
        _writer = OpenAndMigrate();
        _reader1 = OpenReadOnly();
        _reader2 = OpenReadOnly();
    }

    // Writer (单写模式)
    public SqliteConnection Writer => _writer;

    // Round-robin readers
    public SqliteConnection GetReader()
    {
        var idx = Interlocked.Increment(ref _readerIndex) & 1;
        return idx == 0 ? _reader1 : _reader2;
    }
    private int _readerIndex;

    // 自动迁移
    private void RunMigrations(SqliteConnection conn)
    {
        // 从资源文件加载 schema.sql 并按版本执行
    }

    // 批量操作 (事务内)
    public async Task InTransactionAsync(Func<SqliteConnection, Task> action)
    {
        await using var tx = await _writer.BeginTransactionAsync();
        await action(_writer);
        await tx.CommitAsync();
    }
}
```

---

### 1.2 统一缓存层

#### 现状

所有缓存散落在各处，各有不同的接口和淘汰策略。

#### 目标

单一 `LTAICache<TKey, TValue>` + 三个命名变体。

#### 缓存统一接口

```csharp
// src/LTAI.Core/Caching/LTAICache.cs
namespace LTAI.Core.Caching;

public sealed class LTAICache<TKey, TValue> where TKey : notnull
{
    private readonly ConcurrentDictionary<TKey, CacheEntry<TValue>> _store = new();
    private readonly long _maxSizeBytes;
    private readonly TimeSpan _defaultTtl;
    private long _currentSizeBytes;
    private readonly Timer _evictionTimer;

    public LTAICache(CacheOptions options)
    {
        _maxSizeBytes = options.MaxSizeBytes ?? 256 * 1024 * 1024; // 256MB default
        _defaultTtl = options.DefaultTtl ?? TimeSpan.FromMinutes(5);
        _evictionTimer = new Timer(EvictExpired, null,
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    public bool TryGet(TKey key, [MaybeNullWhen(false)] out TValue value)
    {
        if (_store.TryGetValue(key, out var entry) && !entry.IsExpired)
        {
            entry.Access();
            Interlocked.Increment(ref Metrics.Hits);
            value = entry.Value;
            return true;
        }
        Interlocked.Increment(ref Metrics.Misses);
        value = default;
        return false;
    }

    public void Set(TKey key, TValue value, TimeSpan? ttl = null)
    {
        var entry = new CacheEntry<TValue>(value, ttl ?? _defaultTtl);
        var size = EstimateSize(key, value);
        // Evict if needed
        while (_currentSizeBytes + size > _maxSizeBytes && !_store.IsEmpty)
        {
            EvictOne();
        }
        Interlocked.Add(ref _currentSizeBytes, size);
        _store[key] = entry;
    }

    public CacheMetrics Metrics { get; } = new();

    private long EstimateSize(TKey key, TValue value) { /* Marshal.SizeOf heuristic */ }
    private void EvictOne() { /* LRU eviction */ }
    private void EvictExpired(object? state) { /* bulk expired removal */ }
}

public sealed class CacheEntry<T>
{
    private volatile int _lastAccess; // Environment.TickCount
    public T Value { get; }
    public long ExpiresAt { get; }
    public bool IsExpired => Environment.TickCount > ExpiresAt;
    public void Access() => _lastAccess = Environment.TickCount;
    public int LastAccess => _lastAccess;
}

public sealed class CacheMetrics
{
    public long Hits, Misses, Evictions;
    public double HitRate => Hits + Misses > 0 ? (double)Hits / (Hits + Misses) : 0;
}

public sealed class CacheOptions
{
    public long? MaxSizeBytes { get; init; }
    public TimeSpan? DefaultTtl { get; init; }
    public int? MaxEntries { get; init; }   // 替代基于大小的限制
}
```

#### 三个命名变体（DI 注册）

```csharp
// DI 注册
services.AddSingleton<LTAICache<string, LLMResponse>>(_ =>
    new LTAICache<string, LLMResponse>(new CacheOptions
    {
        MaxSizeBytes = 64 * 1024 * 1024,   // 64MB LLM 响应缓存
        DefaultTtl = TimeSpan.FromMinutes(5)
    }));
services.AddSingleton<LTAICache<string, float[]>>(_ =>
    new LTAICache<string, float[]>(new CacheOptions
    {
        MaxSizeBytes = 128 * 1024 * 1024,  // 128MB 嵌入缓存
        DefaultTtl = TimeSpan.FromHours(24)
    }));
services.AddSingleton<LTAICache<string, SafetyVerdict>>(_ =>
    new LTAICache<string, SafetyVerdict>(new CacheOptions
    {
        MaxSizeBytes = 8 * 1024 * 1024,    // 8MB 安全裁决缓存
        DefaultTtl = TimeSpan.FromSeconds(60),
        MaxEntries = 2000
    }));
```

#### 迁移步骤

| 旧缓存 | 新缓存 | 迁移难度 |
|--------|--------|----------|
| ResponseCacheManager | LTAICache<LLMResponse> | 低 |
| ToolEmbeddingCache | LTAICache<float[]> + JSON 持久化 | 中 |
| RemoteEmbeddingCache | LTAICache<float[]> | 低 |
| VerdictCache | LTAICache<SafetyVerdict> | 低 |
| KgStore result cache | LTAICache<GraphResult> | 低 |
| CgGraph query cache | LTAICache<CgResult> | 低 |
| SecretManager cache | LTAICache<string> | 低 |
| BM25 inverted index | 独立（结构特殊） | 不变 |

---

### 1.3 合并 Agent

#### 合并映射表

| 旧 Agent | 合并为 | 保留能力 | 删除的文件 |
|----------|--------|----------|------------|
| LTAI-Code | **LTAI-Dev** | code + frontend + api 工具集 | `code.agent.md`, `frontend.agent.md`, `api.agent.md` |
| LTAI-Frontend | LTAI-Dev | 「前端」作为 domain 子集 | (同上) |
| LTAI-API | LTAI-Dev | OpenAPI/Swagger 能力 | (同上) |
| LTAI-SQL | **LTAI-Data** | SQL + data 分析 | `sql.agent.md`, `data.agent.md` |
| LTAI-Data | LTAI-Data | (工具合并) | (同上) |
| LTAI-Review | **LTAI-QA** | review + test + debug | `review.agent.md`, `test.agent.md`, `debug.agent.md` |
| LTAI-Test | LTAI-QA | (工具合并) | (同上) |
| LTAI-Debug | LTAI-QA | (工具合并) | (同上) |
| LTAI-DevOps | **LTAI-Ops** | devops + security | `devops.agent.md`, `security.agent.md` |
| LTAI-Security | LTAI-Ops | (安全子集) | (同上) |
| LTAI-Chat | **LTAI-Chat** | 通用对话 (Pro 模式) | `chat-pro.agent.md` |
| LTAI-Chat-Pro | LTAI-Chat | Pro 作为 YAML 开关 | (同上) |
| LTAI-LLM | **删除** | 能力并入 LTAI-Dev | `llm.agent.md` |
| LTAI-DCI | **删除** | 设计模式能力并入 LTAI-Arch | `dci.agent.md` |
| LTAI-Plan | **删除** | 计划能力并入 LTAI-Chat | `plan.agent.md` |
| LTAI-Scrum-Master | **删除** | 敏捷能力并入 LTAI-Chat | `scrum-master.agent.md` |
| LTAI-Explore | **保留** | FastContext 委托探索 | (不变) |
| LTAI-Arch | **保留** | 架构设计 | (不变) |
| LTAI-Writer | **保留** | 写作 | (不变) |
| LTAI-Math | **保留** | 数学 | (不变) |
| LTAI-System | **保留** | 系统管理 | (不变) |
| LTAI-Office | **保留** | Office 文档 | (不变) |

#### 合并后清单

```
agents/
├── chat.agent.md        # LTAI-Chat (含 Pro 模式)
├── dev.agent.md          # LTAI-Dev (Code + Frontend + API + LLM)
├── data.agent.md         # LTAI-Data (SQL + 数据分析)
├── qa.agent.md           # LTAI-QA (Review + Test + Debug)
├── ops.agent.md          # LTAI-Ops (DevOps + Security)
├── explore.agent.md      # LTAI-Explore (FastContext 委托)
├── arch.agent.md         # LTAI-Arch (架构设计)
├── writer.agent.md       # LTAI-Writer (写作)
├── math.agent.md         # LTAI-Math (数学)
├── system.agent.md       # LTAI-System (系统管理)
└── office.agent.md       # LTAI-Office (Office 文档)
```

22 → 12（含探索 Agent），-45%。

#### 合并后的 Agent YAML 格式示例

```yaml
# agents/dev.agent.md
---
name: LTAI-Dev
version: 2.0.0
description: "全栈开发专家: 代码、前端、API、LLM 应用"
tools:
  - file            # 读写文件
  - search          # 搜索代码
  - git             # 版本控制
  - shell           # 命令执行
  - code            # 代码分析 (TreeSitter + Roslyn)
  - web             # 网页抓取
  - api             # API 设计/调试
  - database        # 数据库查询
  - frontend        # 前端开发
  - llm             # LLM 应用开发
domains:
  - code            # 后端代码
  - frontend        # 前端代码 (React/Vue)
  - api             # API 设计
  - llm-app         # LLM 应用
context_mode: full    # 全上下文
pro_mode: false       # 默认为 L1
---
你是一个全栈开发专家...
```

---

### 1.4 简化记忆系统

#### 三层记忆设计

```csharp
// src/LTAI.Agent/Memory/IMemoryStore.cs
namespace LTAI.Agent.Memory;

public interface IMemoryStore
{
    // L0: Short-term — 当前会话滑动窗口
    Task StoreMessageAsync(string traceId, ChatMessage message);
    Task<IReadOnlyList<ChatMessage>> GetRecentMessagesAsync(
        string traceId, int count = 20);

    // L1: Long-term — 持久化 FTS5 + 向量检索
    Task StoreFactAsync(MemoryFact fact);
    Task<IReadOnlyList<MemoryFact>> SearchFactsAsync(
        string query, int topK = 10, MemoryFilter? filter = null);

    // L2: Synthesis — 反射/合成记忆
    Task<SynthesizedMemory?> SynthesizeAsync(string topic);
    Task<IReadOnlyList<SynthesizedMemory>> GetAllSynthesesAsync();
}

public sealed record MemoryFact(
    string Id,
    string Content,
    string Room,         // general / coding / reflection
    float Importance,
    IReadOnlyList<string> Entities,
    float[]? Embedding,
    DateTime CreatedAt);

public sealed record SynthesizedMemory(
    string Topic,
    string Summary,
    IReadOnlyList<string> SourceFactIds,
    IReadOnlyList<string> Entities,
    DateTime CreatedAt);

public sealed record MemoryFilter(
    string? Room = null,
    float? MinImportance = null,
    DateTime? Since = null,
    string? EntityName = null);
```

#### 删除的类

| 删除文件 | 能力转移到 |
|----------|-----------|
| `MemoryRefinery.cs` | L2 SynthesizeAsync |
| `ContextOffloader.cs` | 并入 CompactionStep |
| `MermaidStateTracker.cs` | 移除（token 节省由 LTAICache 负责） |
| `PalaceStore.cs` | IMemoryStore 实现 |
| `PalaceFeedbackTracker.cs` | IMemoryStore.StoreFactAsync |
| `PalaceL0Store.cs` | IMemoryStore.StoreMessageAsync |
| `L0IdentityProvider.cs` | 简化为 IdentityProvider |
| `L1EssentialProvider.cs` | L1 层 |
| `L3OnDemandProvider.cs` | L2 层 |
| `L4DeepSearchProvider.cs` | SearchFactsAsync |
| `L6AgentDiaryProvider.cs` | 删除 |
| `ProvenanceProvider.cs` | 删除 |

**代码量预估**：42 文件/2,500 行 → 8 文件/1,200 行，-52%。

---

## Phase 2: 性能优化（2-3 周）

### 2.1 Pipeline DAG 并行化

#### 现状：全串行 15 步

```
Pre: LoraAdapter → MemCaching(Restore) → RagContext → ProactiveSuggest → SafetyCheck → Router → ToolExecution
                                                              ↓
Post: MemCaching(Save) → Compaction → GrammarCheck → AntiPattern → QualityGate → DoDCheck → Retrospective
```

#### 目标：DAG 调度

```
┌─────────────────── Pre-Generation ───────────────────┐
│                                                        │
│  ┌──────────┐  ┌──────────┐  ┌──────────┐             │
│  │ Safety   │  │  Lora    │  │  Mem     │             │
│  │ Check    │  │  Adapter │  │  Cache   │             │
│  └─────┬────┘  └─────┬────┘  │(Restore) │             │
│        │              │       └─────┬────┘             │
│        └──────┬───────┘            │                    │
│               │                    │                    │
│        ┌──────▼────────────────────▼───┐               │
│        │        RagContext             │               │
│        └───────────────┬───────────────┘               │
│                        │                                │
│        ┌───────────────▼───────────────┐               │
│        │     ProactiveSuggest         │               │
│        └───────────────┬───────────────┘               │
│                        │                                │
│        ┌───────────────▼───────────────┐               │
│        │       Router + ToolExec      │               │
│        └───────────────┬───────────────┘               │
│                        │                                │
│                    [LLM Call]                           │
│                        │                                │
└────────────────────────┼───────────────────────────────┘
                         │
┌─────────────────── Post-Generation ──────────────────┐
│                        │                                │
│        ┌───────────────▼───────────────┐               │
│        │     MemCache(Save)           │               │
│        └───────────────┬───────────────┘               │
│                        │                                │
│        ┌───────────────▼───────────────┐               │
│        │      Compaction              │               │
│        └───────────────┬───────────────┘               │
│                        │                                │
│        ┌───────────────▼───────────────────┐           │
│        │  Grammar + AntiPattern + Quality  │ ← PARALLEL│
│        │  (3 steps merge into 1)           │           │
│        └───────────────┬───────────────────┘           │
│                        │                                │
│        ┌───────────────▼───────────────┐               │
│        │       DoDCheck               │               │
│        └───────────────┬───────────────┘               │
│                        │                                │
│        ┌───────────────▼───────────────┐               │
│        │    Retrospective              │               │
│        └───────────────────────────────┘               │
└────────────────────────────────────────────────────────┘
```

#### PipelineStep DAG 定义

```csharp
// src/LTAI.Agent/Pipeline/PipelineDag.cs
namespace LTAI.Agent.Pipeline;

public sealed class PipelineDag
{
    private readonly Dictionary<string, DagNode> _nodes = new();

    public PipelineDag FromSteps(IEnumerable<IPipelineStep> steps)
    {
        // Auto-detect dependencies from attributes
        foreach (var step in steps)
        {
            var attr = step.GetType().GetCustomAttribute<DependsOnAttribute>();
            _nodes[step.Name] = new DagNode(
                step,
                dependsOn: attr?.Dependencies ?? Array.Empty<string>()
            );
        }
        return this;
    }

    public async Task<PipelineResult> ExecuteAsync(
        MessageContext ctx, CancellationToken ct)
    {
        // Kahn's algorithm for topological sort
        var inDegree = new Dictionary<string, int>();
        var ready = new Channel<DagNode>(Channel.CreateUnbounded<DagNode>());

        foreach (var (name, node) in _nodes)
        {
            inDegree[name] = node.DependsOn
                .Count(d => _nodes.ContainsKey(d));
            if (inDegree[name] == 0)
                await ready.Writer.WriteAsync(node, ct);
        }
        ready.Writer.Complete();

        var completedCount = 0;
        var totalNodes = _nodes.Count;
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Environment.ProcessorCount,
            CancellationToken = ct
        };

        await Parallel.ForEachAsync(
            ready.Reader.ReadAllAsync(ct),
            parallelOptions,
            async (node, token) =>
            {
                await node.Step.ProcessAsync(ctx);

                // Decrement dependents and enqueue ready ones
                lock (_nodes)
                {
                    foreach (var (name, next) in _nodes)
                    {
                        if (next.DependsOn.Contains(node.Step.Name))
                        {
                            if (Interlocked.Decrement(ref inDegree[name]) == 0)
                                await ready.Writer.WriteAsync(next, token);
                        }
                    }
                }

                Interlocked.Increment(ref completedCount);
            });

        return new PipelineResult(ctx);
    }
}

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class DependsOnAttribute : Attribute
{
    public string[] Dependencies { get; }
    public DependsOnAttribute(params string[] dependencies)
        => Dependencies = dependencies;
}

// Example usage:
[DependsOn]  // no deps = root
public sealed class SafetyCheckStep : IPipelineStep { ... }

[DependsOn("SafetyCheck", "LoraAdapter")]
public sealed class RagContextStep : IPipelineStep { ... }

[DependsOn("Compaction")]
public sealed class GrammarCheckStep : IPipelineStep { ... }
```

#### 预期加速比

| 场景 | 串行 | DAG | 加速 |
|------|------|-----|------|
| 简单查询（仅 Safety+Router） | 3 步 | 2 层 | 1.5x |
| 完整 Pre-gen | 7 步 | 4 层 | 1.75x |
| 完整 Post-gen | 6 步 | 3 层 | 2x |
| 全 Pipeline | 15 步 | 6 层 | 2.5x |
| 含并行 Grammar+Quality | 3 步 | 1 层 | 3x |

#### 合并 GrammarCheck + AntiPattern + QualityGate

这三个步骤都分析生成的文本，无相互依赖。合并为单个 `TextQualityStep`：

```csharp
[DependsOn("Compaction")]
public sealed class TextQualityStep : IPipelineStep
{
    public async Task ProcessAsync(MessageContext ctx)
    {
        var text = ctx.GetGeneratedText();

        // 并行执行三个检查
        var grammarTask = GrammarCheckAsync(text, ct);
        var antiPatternTask = AntiPatternCheckAsync(text, ct);
        var qualityTask = QualityGateCheckAsync(text, ctx, ct);

        await Task.WhenAll(grammarTask, antiPatternTask, qualityTask);

        var (grammarResult, antiResult, qualityResult) =
            (grammarTask.Result, antiPatternTask.Result, qualityTask.Result);

        // 合并结果
        if (!grammarResult.IsPass)
        {
            ctx.SetBlocked("GrammarCheckBlocked", grammarResult.Errors);
            return;
        }
        if (!antiResult.IsPass)
        {
            ctx.SetBlocked("AntiPatternBlocked", antiResult.Issues);
            return;
        }
        if (qualityResult.Score < _passThreshold)
        {
            ctx.SetBlocked("QualityGateBlocked", qualityResult.Score);
        }
    }
}
```

#### ChannelPipeline 流式处理

```csharp
// src/LTAI.Agent/Pipeline/ChannelPipeline.cs
// 使用 System.Threading.Channels 实现步骤间流式通信

public sealed class ChannelPipeline
{
    private readonly List<PipelineStage> _stages = new();

    public ChannelPipeline AddStage<T>() where T : IPipelineStep
    {
        _stages.Add(new PipelineStage(typeof(T), _stages.Count));
        return this;
    }

    public async IAsyncEnumerable<MessageContext> ExecuteAsync(
        MessageContext ctx,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var channel = Channel.CreateBounded<MessageContext>(
            new BoundedChannelOptions(2)
            {
                FullMode = BoundedChannelFullMode.DropOldest
            });

        // Producer: run stages
        var producer = Task.Run(async () =>
        {
            var current = ctx;
            foreach (var stage in _stages)
            {
                var step = (IPipelineStep)ActivatorUtilities
                    .CreateInstance(_serviceProvider, stage.StepType);
                await step.ProcessAsync(current);
                channel.Writer.TryWrite(current);
                if (current.IsBlocked) break;
            }
            channel.Writer.Complete();
        }, ct);

        // Consumer: yield intermediate results
        await foreach (var snapshot in channel.Reader.ReadAllAsync(ct))
            yield return snapshot;

        await producer;
    }
}
```

---

### 2.2 减少 IChatClient 包装层

#### 现状

```
SubagentContextIsolation → ToolFilteringChatClient → LlmLoggingChatClient
→ ProgressGuardChatClient → ThinkingTagValidator → MultiProviderChatClient
→ MetricsChatClient → SafeChatClient → ThinkingChatClient → ProviderClientManager
→ HTTP
```

#### 目标

```
MonitoringChatClient → MultiProviderChatClient → ProviderClientManager → HTTP
```

`MonitoringChatClient` 合并以下功能：
- Metrics (原 MetricsChatClient)
- Safety (原 SafeChatClient, 通过 SafetyCoordinator 判断)
- Logging (原 LlmLoggingChatClient)
- Progress (原 ProgressGuardChatClient)
- Thinking 标记 (原 ThinkingTagValidator + ThinkingChatClient)

#### MonitoringChatClient 设计

```csharp
// src/LTAI.AI/MonitoringChatClient.cs
namespace LTAI.AI;

public sealed class MonitoringChatClient : IChatClient
{
    private readonly IChatClient _inner;
    private readonly SafetyCoordinator _safety;
    private readonly ILogger _logger;
    private readonly ProgressTracker _progress;

    public MonitoringChatClient(
        IChatClient inner,
        SafetyCoordinator safety,
        ILogger<MonitoringChatClient> logger)
    {
        _inner = inner;
        _safety = safety;
        _logger = logger;
        _progress = new ProgressTracker();
    }

    public async Task<ChatResponse> GetResponseAsync(
        IReadOnlyList<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken ct = default)
    {
        // 1. 注入 thinking 标记 (原 ThinkingTagValidator)
        options = InjectThinkingFlag(options);

        // 2. 进度追踪 (原 ProgressGuardChatClient)
        _progress.StartTracking();

        // 3. 获取响应
        var sw = Stopwatch.StartNew();
        var response = await _inner.GetResponseAsync(messages, options, ct);
        sw.Stop();

        // 4. 输出安全检查 (原 SafeChatClient)
        var safetyResult = await _safety.AuditOutputAsync(
            response.Message.Text, ct);
        if (safetyResult.IsBlocked)
            return CreateBlockedResponse();

        // 5. 指标记录 (原 MetricsChatClient)
        RecordMetrics(response, sw.Elapsed);

        // 6. 日志 (原 LlmLoggingChatClient)
        _logger.LogDebug("LLM call: {Duration}ms, {Tokens} tokens",
            sw.ElapsedMilliseconds, response.Usage?.TotalTokenCount);

        // 7. 进度完成
        _progress.Complete();

        return response;
    }

    // Streaming 同步简化
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IReadOnlyList<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var update in _inner
            .GetStreamingResponseAsync(messages, options, ct))
        {
            yield return update;
        }
    }

    // 委托 GetService
    public object? GetService(Type serviceType, object? state = null)
        => _inner.GetService(serviceType, state);
}
```

**包装层数**: 10 → 3（-70%），每层序列化开销消除。

---

### 2.3 嵌入流水线短路

#### 现状

```
T1: ONNX → T2: RemoteCache → T3: RemoteAPI → T4: GloVe → T5: BM25 → T6: FastEmb
```

每层都跑一轮，即使 ONNX 成功。

#### 改进

```csharp
// src/LTAI.AI/EmbeddingPipeline.cs
public sealed class EmbeddingPipeline
{
    private readonly LocalEmbedder _onnx;
    private readonly EmbeddingApiClient _api;
    private readonly Glove50Embedder _glove;
    private readonly LTAICache<string, float[]> _cache;

    public async Task<float[]> EmbedAsync(
        string text, EmbeddingQuality minQuality, CancellationToken ct)
    {
        // 1. Cache check (always first)
        if (_cache.TryGet(text, out var cached))
            return cached;

        // 2. ONNX (fastest, prefer GPU)
        if (minQuality <= EmbeddingQuality.High && _onnx.Available)
        {
            var result = await _onnx.EmbedAsync(text, ct);
            if (result != null)
            {
                _cache.Set(text, result, ttl: TimeSpan.FromHours(24));
                return result;
            }
        }

        // 3. Remote API (medium quality)
        if (minQuality <= EmbeddingQuality.Medium)
        {
            var result = await _api.EmbedAsync(text, ct);
            if (result != null)
            {
                _cache.Set(text, result, ttl: TimeSpan.FromHours(6));
                return result;
            }
        }

        // 4. GloVe fallback (low quality, always available)
        return _glove.Embed(text);
    }
}

public enum EmbeddingQuality
{
    Low = 0,      // GloVe-only
    Medium = 1,   // Remote API fallback
    High = 2,     // ONNX preferred
}
```

**短路逻辑**：逐级尝试，命中即返；下一级仅当前一级失败或质量要求更高时调用。

---

### 2.4 延迟初始化 Provider

#### 现状

20 个 `AIContextProvider` 在 `AgentContextProviderBuilder` 中全部构造。

#### 改进

```csharp
// src/LTAI.Agent/Context/LazyContextProvider.cs
public sealed class LazyContextProvider : AIContextProvider
{
    private readonly Lazy<Task<AIContextProvider>> _inner;
    private readonly ILogger _logger;

    public LazyContextProvider(
        string name,
        Func<IServiceProvider, Task<AIContextProvider>> factory,
        IServiceProvider sp,
        ILogger<LazyContextProvider> logger)
    {
        Name = name;
        _inner = new Lazy<Task<AIContextProvider>>(
            () => factory(sp), LazyThreadSafetyMode.ExecutionAndPublication);
        _logger = logger;
    }

    public override string Name { get; }

    public override async Task ProvideAIContextAsync(AIContext context)
    {
        var sw = Stopwatch.StartNew();
        var provider = await _inner.Value;
        sw.Stop();

        if (sw.ElapsedMilliseconds > 100)
            _logger.LogWarning(
                "Lazy provider {Name} took {Ms}ms to init",
                Name, sw.ElapsedMilliseconds);

        await provider.ProvideAIContextAsync(context);
    }
}
```

#### 懒加载的 Provider

```csharp
// AgentBuilder.Memory.cs (替换)
providers.Add(new LazyContextProvider("CgGraph", async sp =>
{
    var cg = sp.GetRequiredService<CgGraph>();
    await cg.InitializeAsync();
    return cg;
}, sp, logger));

providers.Add(new LazyContextProvider("KbGraph", async sp =>
{
    var kb = sp.GetRequiredService<KbGraph>();
    await kb.InitializeAsync();
    return kb;
}, sp, logger));

providers.Add(new LazyContextProvider("WasmtimeSandbox", async sp =>
{
    var ws = sp.GetRequiredService<WasmtimeSandbox>();
    await ws.InitializeAsync();
    return ws;
}, sp, logger));
```

**效果**：
- `CgGraph` 初始化（~200ms）只在首次代码查询时触发
- `KbGraph` 初始化（~150ms）只在首次知识查询时触发
- `Wasmtime` 初始化（~500ms）只在首次沙箱执行时触发
- 启动时间预计减少 30-50%

---

### 2.5 LZ4 序列化

#### 现状

`CompressedSessionSerializer` 使用 GZip。

#### 改进

```csharp
// src/LTAI.Core/Session/Lz4SessionSerializer.cs
public sealed class Lz4SessionSerializer : ISessionSerializer
{
    public string FileExtension => ".lz4";

    public byte[] Serialize(ISessionHandle session)
    {
        var json = session.SerializeToJson();
        var bytes = Encoding.UTF8.GetBytes(json);
        return LZ4Pickler.Pickle(bytes);
    }

    public ISessionHandle Deserialize(byte[] data, string name)
    {
        var bytes = LZ4Pickler.Unpickle(data);
        var json = Encoding.UTF8.GetString(bytes);
        var handle = new JsonSessionHandle(name);
        handle.UpdateFromJson(json);
        return handle;
    }
}
```

#### 对比 (10KB 会话)

| 压缩器 | 大小 | 压缩速度 | 解压速度 |
|--------|------|----------|----------|
| GZip (现状) | 3.2KB | 50 MB/s | 100 MB/s |
| Brotli | 2.8KB | 15 MB/s | 30 MB/s |
| **LZ4** | **4.1KB** | **500 MB/s** | **1.2 GB/s** |

**选择 LZ4**：速度优先（读写一次会话 < 1ms vs GZip 的 5-10ms）。

---

## Phase 3: 架构去耦合（2-3 周）

### 3.1 MAF 抽象层

#### MAF 抽象接口（LTAI.Core 定义）

```csharp
// src/LTAI.Core/Agent/IAgentHost.cs
namespace LTAI.Core.Agent;

/// <summary>Agent 宿主抽象 — 替换 MAF HarnessAgent</summary>
public interface IAgentHost
{
    string Name { get; }
    IReadOnlyList<IAgentTool> Tools { get; }
    Task<AgentResponse> ExecuteAsync(
        AgentRequest request, CancellationToken ct);
    IAsyncEnumerable<AgentUpdate> ExecuteStreamingAsync(
        AgentRequest request, CancellationToken ct);
}

public sealed record AgentRequest(
    string ConversationId,
    IReadOnlyList<ChatMessage> Messages,
    AgentContext Context,
    ChatOptions? Options = null);

public sealed record AgentResponse(
    ChatMessage Message,
    AgentMetadata Metadata);

public sealed record AgentUpdate(
    string? TextDelta,
    ToolCall? ToolCall,
    AgentState? State);

public sealed record AgentMetadata(
    string AgentName,
    string Provider,
    string Model,
    int PromptTokens,
    int CompletionTokens,
    TimeSpan Duration,
    bool IsEscalated);
```

#### MAF 适配器（新项目或 LTAI.Agent 内的独立命名空间）

```csharp
// src/LTAI.Agent/Adapter/MafAgentHost.cs
namespace LTAI.Agent.Adapter;

public sealed class MafAgentHost : IAgentHost
{
    private readonly HarnessAgent _inner;
    private readonly IToolRegistry _toolRegistry;

    public string Name => _inner.Name;
    public IReadOnlyList<IAgentTool> Tools =>
        _toolRegistry.GetToolsForAgent(_inner.Name);

    public async Task<AgentResponse> ExecuteAsync(
        AgentRequest request, CancellationToken ct)
    {
        // 1. Convert AgentRequest → MAF 消息格式
        var mafMessages = request.Messages
            .Select(ToMafMessage).ToList();
        var mafOptions = new HarnessOptions
        {
            Model = request.Options?.ModelId,
            Temperature = request.Options?.Temperature,
            MaxTokens = request.Options?.MaxOutputTokens,
        };

        // 2. Call HarnessAgent
        var result = await _inner.ExecuteAsync(
            new AgentRequest(mafMessages, mafOptions), ct);

        // 3. Convert back
        return new AgentResponse(
            ToChatMessage(result.Message),
            new AgentMetadata(
                _inner.Name, result.Provider, result.Model,
                result.Usage.InputTokens, result.Usage.OutputTokens,
                result.Duration, false));
    }

    public IAsyncEnumerable<AgentUpdate> ExecuteStreamingAsync(
        AgentRequest request, CancellationToken ct)
    {
        // 类似转换 ...
    }
}
```

#### DI 注册变更

```csharp
// 旧: 直接注册 HarnessAgent
services.AddKeyedSingleton<HarnessAgent>("LTAI-Code");

// 新: 注册 IAgentHost
services.AddKeyedSingleton<IAgentHost>("LTAI-Dev", (sp, key) =>
{
    var mafAgent = BuildHarnessAgent(sp, "LTAI-Dev");
    return new MafAgentHost(mafAgent, sp.GetRequiredService<IToolRegistry>());
});
```

#### 不删除 MAF

保持 MAF 作为默认实现，但新增抽象层使得可以：
1. 用轻量级实现替换（如直接调用 OpenAI SDK）
2. 在 MAF 有 breaking change 时不影响业务代码
3. 为 WASM 插件化铺路

---

### 3.2 Provider 插件化

#### ILlmProvider 接口

```csharp
// src/LTAI.Core/LLM/ILlmProvider.cs
namespace LTAI.Core.LLM;

public interface ILlmProvider
{
    string Id { get; }           // "deepseek", "openai", "ollama"
    string DisplayName { get; }  // "DeepSeek", "OpenAI", "Ollama"
    bool IsAvailable { get; }    // API key configured?
    int Priority { get; }        // Lower = preferred

    // 模型列表
    IReadOnlyList<ModelInfo> Models { get; }

    // 创建聊天客户端
    IChatClient CreateChatClient(string modelId, ChatOptions? options);

    // 创建嵌入客户端
    IEmbedder? CreateEmbedder();

    // 健康检查
    Task<bool> CheckHealthAsync(CancellationToken ct);
}
```

#### JSON 插件声明

```json
// models/providers/deepseek.json
{
  "id": "deepseek",
  "type": "LTAI.Providers.DeepSeek, LTAI.Providers",
  "displayName": "DeepSeek",
  "priority": 10,
  "config": {
    "apiKey": "DEEPSEEK_API_KEY",
    "baseUrl": "https://api.deepseek.com/v1",
    "models": [
      { "id": "deepseek-v4-flash", "name": "DeepSeek V4 Flash", "context": 131072 },
      { "id": "deepseek-pro", "name": "DeepSeek Pro", "context": 131072 }
    ]
  }
}
```

```json
// models/providers/ollama.json
{
  "id": "ollama",
  "type": "LTAI.Providers.Ollama, LTAI.Providers",
  "displayName": "Ollama",
  "priority": 100,
  "config": {
    "baseUrl": "http://localhost:11434/v1",
    "models": [
      { "id": "qwen3-8b", "name": "Qwen3 8B", "context": 131072 },
      { "id": "deepseek-r1-7b", "name": "DeepSeek R1 7B", "context": 32768 }
    ]
  }
}
```

#### 插件加载器

```csharp
// src/LTAI.Agent/Providers/ProviderLoader.cs
public sealed class ProviderLoader
{
    private readonly string _providersDir = Path.Combine(
        AppContext.BaseDirectory, "models", "providers");

    public async Task<IReadOnlyList<ILlmProvider>> LoadAllAsync()
    {
        var providers = new List<ILlmProvider>();

        foreach (var file in Directory.EnumerateFiles(
            _providersDir, "*.json"))
        {
            var json = await File.ReadAllTextAsync(file);
            var manifest = JsonSerializer.Deserialize<ProviderManifest>(json);

            var type = Type.GetType(manifest.Type);
            if (type == null) continue;

            var provider = (ILlmProvider)Activator.CreateInstance(
                type, manifest.Config);
            providers.Add(provider);
        }

        return providers.OrderBy(p => p.Priority).ToList();
    }
}
```

---

### 3.3 工具接口标准化

#### IAgentTool 接口

```csharp
// src/LTAI.Core/Tools/IAgentTool.cs
namespace LTAI.Core.Tools;

public interface IAgentTool
{
    string Name { get; }
    string Description { get; }
    string? Domain { get; }      // "code", "git", "web", etc.
    ToolPermission RequiredPermission { get; }

    // Schema for LLM function calling
    FunctionDefinition GetFunctionDefinition();

    // Execute the tool
    Task<ToolResult> ExecuteAsync(
        ToolContext ctx,
        IReadOnlyDictionary<string, object?> args,
        CancellationToken ct);
}

public sealed record ToolResult(
    bool Success,
    string? Output,
    string? Error = null,
    IReadOnlyDictionary<string, object?>? Metadata = null);

public sealed record ToolContext(
    string AgentName,
    string ConversationId,
    string UserId,
    IToolRegistry Registry);

[Flags]
public enum ToolPermission
{
    Read = 1,
    Write = 2,
    Execute = 4,
    Admin = 8
}
```

#### 工具声明文件

```json
// .livingtree/tools/LTAI-Dev/git-commit.tool.json
{
  "name": "git_commit",
  "description": "提交代码变更到 Git 仓库",
  "domain": "git",
  "permission": "Write",
  "implementation": "LTAI.Agent.Tools.GitTool, LTAI.Agent",
  "examples": [
    "git commit -m 'fix: resolve timeout issue'",
    "提交当前变更"
  ],
  "config": {
    "maxMessageLength": 72
  }
}
```

**热加载机制**（沿用现有 `AgentToolStore` 的 FileSystemWatcher）。

---

## 4. 长期演进

### 4.1 Source Generator DI

```csharp
// 使用 IncrementalGenerator 自动扫描 [Service] 特性

[Service(ServiceLifetime.Singleton)]
public sealed class KgStore : IKgStore { ... }

// 生成的代码 (自动):
partial class LTAIDependencyInjection
{
    public static IServiceCollection AddLTAIServices(
        this IServiceCollection services)
    {
        services.AddSingleton<IKgStore, KgStore>();
        services.AddSingleton<IPalaceStore, PalaceStore>();
        // ... 自动收集所有 [Service] 标记的类
        return services;
    }
}
```

### 4.2 WASM 沙盒插件

将 `WasmtimeSandbox` 改为插件架构，支持 WASI 和 WASM-NN。

### 4.3 OpenTelemetry 第一公民化

```csharp
// 默认开启 OTel，移除 ConditionalFeature flag
services.AddOpenTelemetry()
    .WithTracing(t => t
        .AddSource("LTAI.*")
        .SetSampler<ParentBasedSampler>(new TraceIdRatioBasedSampler(0.1)))
    .WithMetrics(m => m
        .AddMeter("LTAI.*")
        .AddPrometheusExporter());  // 替代 ConsoleExporter
```

---

## 附录：代码量统计

| 项目 | 当前文件数 | 当前行数 | 目标文件数 | 目标行数 | 缩减 |
|------|-----------|---------|-----------|---------|------|
| LTAI.Core | 49 | ~5,000 | 45 | ~4,200 | -16% |
| LTAI.AI | 45 | ~6,500 | 35 | ~4,800 | -26% |
| LTAI.Agent | 120+ | ~20,000 | 70 | ~12,000 | -40% |
| LTAI.Agent (Memory) | 42 | ~2,500 | 8 | ~1,200 | -52% |
| Agents | 32 文件 | ~2,500 | 22 | ~1,500 | -40% |
| **总计** | **~280** | **~36,500** | **~180** | **~23,700** | **-35%** |

## 附录：Phase 交付顺序

```
Phase 0 (1d)
  └── 诊断工具 → 为所有后续决策提供数据

Phase 1 (1-2w)
  ├── 1.4 Memory 简化 ← 无风险，独立
  ├── 1.3 Agent 合并  ← 只需改 YAML + 注册
  ├── 1.2 缓存统一    ← 机械替换
  └── 1.1 SQLite 合并 ← 需迁移数据

Phase 2 (2-3w)
  ├── 2.5 LZ4 序列化  ← 替换实现，2 小时
  ├── 2.4 懒加载      ← 加包装，不改逻辑
  ├── 2.3 嵌入短路    ← 2.2 的前置
  ├── 2.2 减少包装层  ← 高风险，需充分测试
  └── 2.1 DAG Pipeline ← 最核心改动

Phase 3 (2-3w)
  ├── 3.4 消除全局静态 ← 机械重构
  ├── 3.3 工具标准化    ← 接口提取
  ├── 3.2 Provider 插件 ← 新功能，可独立
  └── 3.1 MAF 抽象     ← 最大改动
```

**建议顺序**：Phase 1 → Phase 2(2.5→2.4→2.3→2.1) → Phase 2.2 最后 → Phase 3。
由简到难，每步可独立交付和验证。
