# LTAI Tools Catalog

## Architecture

```
┌──────────────────────────────────────────────────────────────┐
│                    AIToolRegistry                             │
│  (统一注册中心 — 所有工具通过 AIFunctionFactory 注册为 AIFunction) │
├──────────────────────────────────────────────────────────────┤
│                                                              │
│  ┌─────────────────────┐    ┌──────────────────────────────┐ │
│  │  LTAI.MAF.Tools     │    │  LTAI.Capability.Tools       │ │
│  │  (通用工具 43个)     │    │  (领域工具 80+个)             │ │
│  │                     │    │                              │ │
│  │  filesystem_* (6)   │    │  gaussian_plume (大气扩散)    │ │
│  │  shell_* (2)        │    │  aermod_full (EPA AERMOD)    │ │
│  │  http_* (4)         │    │  calpuff_full (CALPUFF)      │ │
│  │  math_* (5)         │    │  gral_dispersion (GRAL)      │ │
│  │  text_* (7)         │    │  geocode / gis_* (空间分析)  │ │
│  │  data_* (5)         │    │  cad_* (CAD导入/分析)        │ │
│  │  datetime_* (4)     │    │  km_* (知识库检索)           │ │
│  │  code_* (3)         │    │  doc_parse (文档解析)        │ │
│  │  env_* (4)          │    │  vfs:* (虚拟文件系统)         │ │
│  │  web_* (3)          │    │  translate / weather / ...   │ │
│  └─────────────────────┘    └──────────────────────────────┘ │
│                                                              │
│  注册顺序: MAF通用工具 → Capability领域工具 (后注册覆盖同名)    │
└──────────────────────────────────────────────────────────────┘
```

> 所有工具均可被 LLM 通过 `FunctionInvokingChatClient` 自动发现和调用。

---

## 一、通用工具 (LTAI.MAF.Tools) — 43 个

### 1.1 文件系统 (filesystem) — 6 个

| 工具名 | 功能 | 参数 |
|--------|------|------|
| `filesystem_read` | 读取文件内容 | `path` — 文件路径 |
| `filesystem_write` | 写入文件（自动创建目录） | `path`, `content` |
| `filesystem_list` | 列出目录内容 | `path`, `pattern?` — 通配符 |
| `filesystem_delete` | 删除文件 | `path` |
| `filesystem_exists` | 检查文件/目录是否存在 | `path` |
| `filesystem_search` | 递归搜索匹配文件 | `rootPath`, `pattern`, `maxResults?` |

### 1.2 Shell 执行 (shell) — 2 个

| 工具名 | 功能 | 参数 |
|--------|------|------|
| `shell_exec` | 执行 Shell 命令（自动拦截危险命令，60s 超时） | `command`, `workingDirectory?` |
| `shell_env` | 获取系统环境和当前目录信息 | — |

### 1.3 HTTP 网络 (http) — 4 个

| 工具名 | 功能 | 参数 |
|--------|------|------|
| `http_get` | HTTP GET 请求 | `url`, `headers?` — JSON 格式 |
| `http_post` | HTTP POST JSON 请求 | `url`, `body`, `headers?` |
| `http_download` | 下载文件返回 Base64 | `url` |
| `http_check` | HEAD 请求检查状态 | `url` |

### 1.4 数学计算 (math) — 5 个

| 工具名 | 功能 | 参数 |
|--------|------|------|
| `math_eval` | 计算数学表达式（支持 sqrt/pow/sin/cos/log/round/pi/e） | `expression` |
| `math_base_convert` | 进制转换（2/8/10/16） | `value`, `fromBase`, `toBase` |
| `math_convert_units` | 单位换算（长度/重量/温度/面积/体积/速度/时间/数据） | `value`, `fromUnit`, `toUnit` |
| `math_random` | 生成随机数 | `min`, `max` |
| `math_statistics` | 统计计算（count/sum/mean/median/min/max/stddev） | `numbersJson` — JSON 数组 |

### 1.5 文本处理 (text) — 7 个

| 工具名 | 功能 | 参数 |
|--------|------|------|
| `text_count` | 统计字数/词数/行数 | `text` |
| `text_hash` | 计算哈希（MD5/SHA1/SHA256/SHA384/SHA512） | `text`, `algorithm?` |
| `text_base64` | Base64 编码/解码 | `text`, `operation` — encode/decode |
| `text_format_json` | 格式化 JSON 字符串 | `json` |
| `text_convert_case` | 大小写转换（upper/lower/title/camel/pascal/snake/kebab） | `text`, `targetCase` |
| `text_regex_replace` | 正则替换 | `text`, `pattern`, `replacement` |
| `text_regex_extract` | 正则提取匹配内容 | `text`, `pattern` |

### 1.6 数据处理 (data) — 5 个

| 工具名 | 功能 | 参数 |
|--------|------|------|
| `data_parse_csv` | 解析 CSV 为 JSON 数组 | `csv`, `delimiter?` |
| `data_query_json` | JSONPath 查询 JSON | `json`, `jsonPath` |
| `data_convert_format` | JSON↔CSV 格式转换 | `data`, `sourceFormat`, `targetFormat` |
| `data_pretty_print` | JSON 美化输出 | `json` |
| `data_pluck` | 从 JSON 数组中提取指定属性 | `jsonArray`, `propertyName` |

### 1.7 日期时间 (datetime) — 4 个

| 工具名 | 功能 | 参数 |
|--------|------|------|
| `datetime_now` | 获取当前时间（UTC/本地/指定时区） | `timezoneOffset?` — "+08:00" |
| `datetime_from_timestamp` | Unix 时间戳→可读日期 | `timestamp`, `unit?` — seconds/milliseconds |
| `datetime_diff` | 两个日期的时间差 | `date1`, `date2` — ISO 8601 |
| `datetime_add` | 日期加减 | `dateStr`, `amount`, `unit` |

### 1.8 代码工具 (code) — 3 个

| 工具名 | 功能 | 参数 |
|--------|------|------|
| `code_stats` | 快速代码统计（行数/语言/函数/类） | `code`, `language?` |
| `code_generate_snippet` | 生成常用代码片段（rest-client/db-query/file-io/sort/filter/json-parse） | `pattern`, `language?` |
| `code_json_to_class` | JSON→类定义（C#/Python/TypeScript） | `json`, `language?`, `className?` |

### 1.9 系统环境 (env) — 4 个

| 工具名 | 功能 | 参数 |
|--------|------|------|
| `env_sysinfo` | 系统详细信息（OS/内存/CPU/磁盘） | — |
| `env_get_var` | 获取环境变量（敏感值自动脱敏） | `name` |
| `env_processes` | 列出运行进程 | `filter?`, `top?` |
| `env_network` | 网络信息 + 可选 Ping | `pingHost?` |

### 1.10 网页搜索 (web) — 3 个

| 工具名 | 功能 | 参数 |
|--------|------|------|
| `web_fetch_page` | 抓取网页纯文本 | `url` |
| `web_extract_metadata` | 提取网页 Meta/OG/RSS 标签 | `url` |
| `web_search` | DuckDuckGo 搜索（无需 API Key） | `query`, `maxResults?` |

---

## 二、领域工具 (LTAI.Capability) — 80+ 个

### 2.1 环境影响评价 EIA (eia / eia_pro) — 21 个

LTAI 的核心领域能力，实现了中国 HJ 2.2-2018 / GB 3095 / GB 3096 / GB 3838 等国家标准。

#### 大气扩散模型

| 工具名 | 模型 | 说明 |
|--------|------|------|
| `gaussian_plume` | 高斯烟羽 | 点源连续排放扩散计算 |
| `gaussian_plume_building` | 建筑物下洗 | 考虑建筑物尾流效应的扩散 |
| `inversion_fumigation` | 熏烟模式 | 逆温层破坏时的污染扩散 |
| `aermod_full` | EPA AERMOD | 美国EPA法规模型（需下载 aermod.exe） |
| `calpuff_full` | CALPUFF | 非稳态拉格朗日长距离输送模型 |
| `gral_dispersion` | GRAL | 复杂地形+建筑物CFD粒子扩散 |

#### 噪声模型

| 工具名 | 说明 |
|--------|------|
| `noise_iso9613` | ISO 9613 户外声传播衰减 |
| `noise_attenuation` | 噪声衰减计算（距离/屏障/地面） |
| `noise_traffic` | 道路交通噪声（HJ 2.4） |

#### 水环境

| 工具名 | 说明 |
|--------|------|
| `streeter_phelps` | Streeter-Phelps DO 氧垂曲线 |
| `river_mixing` | 河流混合过程段计算 |

#### 风险评估

| 工具名 | 说明 |
|--------|------|
| `co2_equivalent` | CO₂ 当量计算（碳达峰碳中和） |
| `hazard_quotient` | 危害商数（健康风险评估） |
| `ecological_risk` | 生态风险评估 |
| `soil_erosion` | 土壤侵蚀模数 |
| `carbon_sink` | 碳汇估算 |

#### 标准查询与分类

| 工具名 | 说明 |
|--------|------|
| `lookup_standard` | 查询 GB/HJ 标准限值 |
| `classify_water_quality` | 水质类别判定（I~V类） |
| `classify_air_quality` | 空气质量类别判定（AQI） |
| `classify_noise_level` | 噪声等级分类 |
| `mathnet_stats` | MathNet 统计计算（数据分布分析） |

### 2.2 GIS 空间分析 (gis) — 6 个

| 工具名 | 功能 |
|--------|------|
| `geocode` | 地址→经纬度正地理编码 |
| `gis_geocode` | 地址解析（增强版） |
| `gis_buffer` | 缓冲区分析 |
| `spatial_search` | 空间搜索 |
| `distance_calc` | 两点距离计算（Haversine/Vincenty） |
| `coordinate_transform` | 坐标系转换（WGS84/GCJ02/BD09） |

### 2.3 知识库检索 (knowledge) — 4 个

| 工具名 | 功能 |
|--------|------|
| `km_search` | Kernel Memory 语义搜索 |
| `km_import` | 导入文档到知识库 |
| `km_ask` | RAG 问答 |
| `vector_search` | 向量相似度搜索 (HNSW) |

### 2.4 代码分析 (code) — 4 个

| 工具名 | 功能 | 实现 |
|--------|------|------|
| `code_analyze` | ⭐ 深度代码分析（多语言/复杂度/依赖图） | MultiLangCodeAnalyzer |
| `code_review` | 代码审查报告（安全/规范/性能） | CodeReviewEngine |
| `sandbox_exec` | 沙箱代码执行（Python/JS/C#/Shell） | ProcessSandbox + DockerSandbox |
| `code_graph` | 代码知识图谱查询 | CodeGraph |

### 2.5 浏览器 (web) — 4 个

| 工具名 | 功能 |
|--------|------|
| `browser_browse` | Playwright 浏览器自动化（访问网页/提取内容） |
| `browser_screenshot` | 网页截图 |
| `web_fetch` | HTTP 抓取网页 HTML |
| `search` | 多源搜索（Web/KB/本地） |

### 2.6 文档处理 (doc) — 8 个

| 工具名 | 功能 |
|--------|------|
| `doc_parse` | 文档解析（JSON/XML/CSV/MD/YAML/HTML/INI） |
| `text_extract` | 文本提取 |
| `report_generate` | 报告生成 |
| `observe_format` | 格式识别 |
| `style_learn` | 风格学习 |
| `visual_render` | 可视化渲染 |
| `vfs:read` / `vfs:write` / ... | 虚拟文件系统操作 |

### 2.7 集成服务 (integration) — 7 个

| 工具名 | 功能 |
|--------|------|
| `email_send` | 发送邮件（SMTP） |
| `sms_send` | 发送短信 |
| `translate` | 文本翻译 |
| `image_search` | 图片搜索（Unsplash/Pixabay） |
| `weather` | 天气查询 |
| `github_status` | GitHub 仓库状态 |
| `search_apis` | Public APIs 目录搜索 |

### 2.8 系统管理 (system) — 7 个

| 工具名 | 功能 |
|--------|------|
| `models_list` / `models_show` / `models_search` / `models_sync` | AI 模型管理 |
| `service_install` / `service_uninstall` / `service_status` | Windows 服务管理 |

### 2.9 VFS 虚拟文件系统 (vfs) — 7 个

| 工具名 | 功能 |
|--------|------|
| `vfs:read` | 读取虚拟文件 |
| `vfs:write` | 写入虚拟文件 |
| `vfs:list` | 列出虚拟目录 |
| `vfs:delete` | 删除虚拟文件 |
| `vfs:exists` | 检查存在性 |
| `vfs:search` | 搜索虚拟文件 |
| `vfs:move` | 移动/重命名虚拟文件 |

### 2.10 其他

| 类别 | 工具 | 功能 |
|------|------|------|
| **推理** | `reason` | 多引擎推理（Math/Logic/Dialectical/Attribution） |
| **CAD** | `cad_import` / `cad_analyze` / `cad_export` | CAD 图纸分析 |
| **CLI** | `cli_wrap_function` 等 5 个 | CLI 工具自动生成和扫描 |
| **Shell** | `cli_execute` | 命令执行 |
| **Git** | `git_diff` / `git_log` / `git_blame` | Git 操作（stub） |
| **Memory** | `remember` / `recall` | 记忆存储/回忆（stub） |
| **通知** | `notify` | 通知推送（stub） |
| **GIS** | `gis_geocode` | 地址→坐标 |

---

## 三、调用方式

### 3.1 LLM 自动调用（Function Calling）

工具已注册到 `AIToolRegistry`，并通过 `LivingTreeSystem` 注入到每次 LLM 请求的 `ChatOptions.Tools` 中。LLM 自动判断何时需要调用工具：

```csharp
// LLM 请求自动携带工具列表
var options = new ChatOptions { 
    ModelId = model, 
    Tools = _toolRegistry.GetTools().ToList()  // 全部 120+ 工具
};
await _llm.GetResponseAsync(messages, options);
```

### 3.2 手动调用

```csharp
var registry = services.GetRequiredService<AIToolRegistry>();
var result = await registry.InvokeAsync("math_eval", new() { ["expression"] = "2 + 3 * 4" });
```

### 3.3 添加新工具

```csharp
// 方式一：纯函数注册
registry.RegisterTool("my_tool", AIFunctionFactory.Create(
    (string input) => ProcessInput(input), 
    "my_tool", 
    "Description of what this tool does"));

// 方式二：类方法注册（推荐用于工具集）
public static class MyTools {
    [Description("My tool description")]
    public static string MyTool([Description("Input")] string input) => ...;
}
```

---

## 四、注册顺序

```
1. LTAI.MAF.Tools (通用工具 43个) ── 基础层
       ↓ 可能被覆盖
2. LTAI.Capability.SeedAllAsync (领域工具 80+个) ── 领域层（覆盖同名）
       ↓ 补充
3. Program.cs 手动注册 (browser_browse, reason, KM工具...)
```

同名工具以后注册者为准。例如 `code_analyze` 由 Capability 的 `MultiLangCodeAnalyzer` 提供深度分析，MAF 的简化版本改名为 `code_stats`。

---

## 五、工具分类统计

| 来源 | 类别 | 数量 |
|------|------|------|
| **MAF.Tools** | filesystem | 6 |
| | shell | 2 |
| | http | 4 |
| | math | 5 |
| | text | 7 |
| | data | 5 |
| | datetime | 4 |
| | code | 3 |
| | env | 4 |
| | web | 3 |
| **MAF 小计** | | **43** |
| **Capability** | EIA 环境模型 | 21 |
| | GIS 空间分析 | 6 |
| | 知识库检索 | 4 |
| | 代码分析 | 4 |
| | 浏览器 | 4 |
| | 文档处理 | 8 |
| | 集成服务 | 7 |
| | 系统管理 | 7 |
| | VFS 虚拟文件系统 | 7 |
| | 推理 | 1 |
| | CAD | 3 |
| | CLI | 5 |
| | Shell/Git/Memory/通知 | 6 |
| **Capability 小计** | | **83** |
| **总计** | | **~126** |
