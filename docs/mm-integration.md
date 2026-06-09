# MetaMessage (mm) C# 实现 & LTAI4Net 集成方案

## 1. 概述

MetaMessage（mm）是一个结构化数据交换协议：自描述、自约束、自示例。本方案设计其 C# 实现的架构，以及如何逐步集成到 LTAI4Net 项目中。

### 1.1 为什么不直接用上游 NuGet 包

| 因素 | 上游 mm-cs (v0.1.20) | 自研方案 |
|------|----------------------|---------|
| API 稳定性 | 0.1.x，月均 5 次 breaking change | 按需设计，锁定接口 |
| 字段命名 | 强制小写 `name` 字段名 | 兼容 LTAI PascalCase 风格 |
| 特性集 | 全量实现（含 CLI、10 语言绑定） | 仅 LTAI 需要的子集 |
| 包体积 | 64KB + 依赖 | ~20KB，零外部依赖 |
| AOT 兼容 | 未验证 | 原生支持 Source-gen |
| License | MIT（可 fork） | MIT |

### 1.2 设计原则

- **渐进集成**：不替换任何现有工作流程，新增选项
- **零外部依赖**：仅 `System.*` 和 `Microsoft.*` 运行时内置库
- **AOT 友好**：Source-generator 可选，运行时反射作为 fallback
- **向上兼容**：MM 数据可以无损转回 JSON，不影响现有持久化数据

---

## 2. 二进制格式（Wire Protocol）

### 2.1 类型系统

```
Prefix（高 4 bits） + 长度/标记（低 4 bits） [+ 数据]
```

| 前缀 | 类型 | 说明 |
|------|------|------|
| `0x0` | POSITIVE_INT | 无符号/正整数 |
| `0x1` | NEGATIVE_INT | 负整数 |
| `0x2` | SIMPLE | 布尔/null/简单值 |
| `0x3` | FLOAT | 浮点数（科学计数法编码） |
| `0x4` | STRING | UTF-8 字符串 |
| `0x5` | BYTES | 字节数组 |
| `0x6` | CONTAINER | 数组/对象容器 |
| `0x7` | TAG | 带标签的负载 |

### 2.2 ValueType 枚举（支持的 20+ 类型）

```
str, bool, i/i8/i16/i32/i64, u/u8/u16/u32/u64,
f32/f64, decimal, bigint, bytes,
datetime, date, time, uuid, ip, url, email, enums,
obj, map, vec, arr, doc, media
```

### 2.3 编码示例

```
整数 42:      0x2A                          （1 字节，SIMPLE 小值优化）
整数 1000:    0x13 0xE8 0x03               （3 字节，POSITIVE_INT + 2 字节 LE）
字符串 "hi":  0x42 0x68 0x69               （3 字节，STRING + 2 字节 UTF-8）
标签负载:      0x7F <len> <tag-bytes> <payload>
```

### 2.4 Tag 语法

```
type=<type>; name=<name>; desc=<desc>; min=<n>; max=<n>;
pattern=<regex>; enums=<a|b|c>; nullable; deprecated;
unique; allow_empty; default=<val>
child_type=<type>; child_desc=<desc>; child_min=<n>; child_max=<n>
```

Tag 在二进制中用 key-value 对序列化，每对 `key=value;` 编码为两个小字符串。

---

## 3. C# 实现架构

```
LTAI.Mm/
├── Core/
│   ├── ValueType.cs          # 枚举：全部支持的类型
│   ├── WireConstants.cs      # 常量：前缀标记、长度阈值
│   ├── WireEncoder.cs        # 二进制编码器（写入 GrowableByteBuf）
│   ├── WireDecoder.cs        # 二进制解码器（读取 ReadOnlySpan<byte>）
│   ├── FloatCodec.cs         # f32/f64 编码：十进制科学计数法 → 紧凑二进制
│   └── BigIntWireCodec.cs    # bigint/decimal 编码：有符号变长
├── Ir/
│   ├── Tag.cs                # 标签解析：Parse(tagString)、序列化、约束校验
│   └── TagAttribute.cs       # [MM] 属性：用于成员标注
├── Tree/
│   ├── INode.cs              # 节点接口
│   ├── NodeScalar.cs         # 标量节点
│   ├── MmArray.cs            # 数组节点
│   ├── MmMap.cs              # 对象节点
│   └── MmDoc.cs              # 文档根节点
├── Jsonc/
│   ├── JsoncParser.cs        # JSONC 解析器（含 `// mm:` 标签提取）
│   └── JsoncEmitter.cs       # INode → JSONC 字符串
├── Reflection/
│   ├── ReflectEncoder.cs     # 反射：任意对象 → INode
│   ├── ReflectBinder.cs      # INode → 绑定到已有对象
│   └── TypeInfer.cs          # C# 类型 → ValueType 推断
├── Converts/
│   ├── MmToJsonConverter.cs  # INode → System.Text.Json 桥接
│   └── JsonToMmConverter.cs  # System.Text.Json → INode
├── MetaMessage.cs             # 门面 API
├── LTAI.Mm.csproj
└── README.md
```

### 3.1 门面 API

```csharp
// 核心序列化
byte[] Encode<T>(T value);
byte[] Encode(object value, Type type);
T Decode<T>(byte[] data);
void Decode(byte[] data, object target);

// 值编码
byte[] FromValue(object value, string? tag = null);

// JSONC 互转
string DecodeToJsonc(byte[] data);
string ValueToJsonc(object value);
byte[] FromJsonc(string jsonc);

// 树操作
INode DecodeToTree(byte[] data);
object? ExtractValue(INode node);

// 校验
ValidationResult Validate(object value, string tag);
```

### 3.2 [MM] Attribute 用法

```csharp
public class User {
    [MM("desc=用户ID")]
    public long Id { get; set; }

    [MM("desc=用户名; min=1; max=50")]
    public string Name { get; set; } = "";

    [MM("type=email; desc=邮箱")]
    public string Email { get; set; } = "";

    [MM("desc=年龄; min=0; max=150")]
    public byte Age { get; set; }

    [MM("-")]  // 排除
    public string Internal { get; set; } = "";
}
```

类型推断规则：`string→str`, `int→i`, `long→i64`, `byte→u8`, `bool→bool`, `float→f32`, `double→f64`, `DateTime→datetime`, `List<T>→vec`, `T[]→arr`, `Dictionary<K,V>→map`。手动指定 `type=` 覆盖推断。

---

## 4. LTAI4Net 集成路线图

### Phase 1 — 核心库（~1-2 天）

- 实现 `LTAI.Mm` 项目（Core + Ir + Tree + MetaMessage 门面）
- 实现 WireEncoder/WireDecoder 全类型编解码
- 实现 Tag 解析器
- 实现 INode 树结构
- 单元测试覆盖 30+ 类型 + 边界条件

### Phase 2 — 反射绑定（~1 天）

- 实现 `[MM]` Attribute
- 实现 ReflectEncoder（对象 → INode）
- 实现 ReflectBinder（INode → 对象）
- 类型推断映射
- 集成测试覆盖常见 DTO

### Phase 3 — JSONC 互转（~1 天）

- 实现 JSONC 解析器（`// mm:` 标签提取）
- 实现 JSONC 到 INode 的转换
- 实现 INode 到 JSONC 的回写
- 桥接 `System.Text.Json` → MM 互转

### Phase 4 — 集成 Session 持久化（~1 天）

**目标文件：** `src/LTAI.Core/Session/JsonSessionHandle.cs`

**改动：**
```csharp
public class JsonSessionHandle {
    // 新增方法（不删旧方法）
    public byte[] SerializeToMm();
    public void UpdateFromMm(byte[] data);
}
```

`SessionManager` 可选使用 MM 格式存储。凭 `ISessionHandle` 接口兼容，不影响现有流程。

### Phase 5 — 集成 Tool IO 合约（~1 天）

**目标文件：** `src/LTAI.Agent/Tools/` 下受影响的工具

**改动：**
- 工具输入/输出 DTO 添加 `[MM]` 属性
- 新增 `MmToolResult` 方法返回 MM 二进制（保留 JSON 路径）
- `FunctionInvokingChatClient` 层可读取 `[MM]` 属性作为 LLM 的 function calling schema

### Phase 6 — 集成配置校验（可选，~0.5 天）

**目标文件：** `src/LTAI.Core/Configuration/LTAIOptions.cs`

**改动：**
- `LTAIOptions` 属性添加 `[MM]` 标签
- 加载时调用 `MetaMessage.Validate()` 自动校验约束

---

## 5. 迁移策略

### 5.1 双轨写入（建议）

```csharp
// SessionManager 写 session 时同时写两种格式
file.WriteAllText(sessionId + ".json", json);      // 旧格式
file.WriteAllBytes(sessionId + ".mm", mmData);      // 新格式

// 读时尝试新格式，fallback 到旧格式
if (File.Exists(sessionId + ".mm")) return ReadMm();
return ReadJson();
```

6 个月后移除 JSON fallback。

### 5.2 不破坏现有接口

所有新 API 以 `*Mm`/`*FromMm` 后缀添加，不修改现有方法签名。

### 5.3 与 System.Text.Json 桥接

```csharp
// MM → JSON（无损，类型信息嵌入注释）
string json = MetaMessage.DecodeToJsonc(mmBytes);

// JSON → MM（如果 JSON 来自旧数据，类型推断使用 C# 反射）
byte[] mm = MetaMessage.FromJsonc(json);
```

---

## 6. 关键决策记录

| 决策 | 选择 | 理由 |
|------|------|------|
| 项目名 | `LTAI.Mm` | `MetaMessage` 过长，`Mm` 简洁且匹配官方缩写 |
| 外部依赖 | 零 | 与 LTAI.Core 一致 |
| 浮点编码 | 十进制科学计数法 | 避免二进制浮点精度问题 |
| 默认命名风格 | PascalCase | 与 LTAI C# 代码风格一致 |
| 反射 vs Source-gen | 运行时反射（Phase1）+ Source-gen（Phase7） | 降低实现复杂度，后续优化 |
| 是否 for 上游 mm-cs 代码 | 参考设计，完全重写 | 避免 License 污染，按需定制 |

---

## 7. 文件清单（新增 ~1500 行）

| 文件 | 预计行数 | 说明 |
|------|---------|------|
| `src/LTAI.Mm/Core/ValueType.cs` | 60 | 类型枚举 + 序列化辅助 |
| `src/LTAI.Mm/Core/WireConstants.cs` | 50 | 所有常量 |
| `src/LTAI.Mm/Core/WireEncoder.cs` | 350 | 二进制编码器 |
| `src/LTAI.Mm/Core/WireDecoder.cs` | 400 | 二进制解码器 |
| `src/LTAI.Mm/Core/FloatCodec.cs` | 80 | 浮点科学计数法编解码 |
| `src/LTAI.Mm/Core/BigIntWireCodec.cs` | 60 | 大整数变长编码 |
| `src/LTAI.Mm/Ir/Tag.cs` | 200 | Tag 解析、序列化、属性访问 |
| `src/LTAI.Mm/Ir/TagAttribute.cs` | 30 | [MM] 属性类 |
| `src/LTAI.Mm/Tree/INode.cs` | 30 | 节点接口 |
| `src/LTAI.Mm/Tree/NodeScalar.cs` | 40 | 标量节点 |
| `src/LTAI.Mm/Tree/MmArray.cs` | 30 | 数组节点 |
| `src/LTAI.Mm/Tree/MmMap.cs` | 40 | 对象节点 |
| `src/LTAI.Mm/Tree/MmDoc.cs` | 20 | 文档根 |
| `src/LTAI.Mm/Jsonc/JsoncParser.cs` | 300 | JSONC 解析器 |
| `src/LTAI.Mm/Jsonc/JsoncEmitter.cs` | 150 | JSONC 输出器 |
| `src/LTAI.Mm/Reflection/ReflectEncoder.cs` | 200 | 反射编码 |
| `src/LTAI.Mm/Reflection/ReflectBinder.cs` | 200 | 反射绑定 |
| `src/LTAI.Mm/Reflection/TypeInfer.cs` | 80 | 类型推断 |
| `src/LTAI.Mm/Converts/MmToJsonConverter.cs` | 80 | MM→JSON 桥接 |
| `src/LTAI.Mm/Converts/JsonToMmConverter.cs` | 80 | JSON→MM 桥接 |
| `src/LTAI.Mm/MetaMessage.cs` | 100 | 门面 API |
| `src/LTAI.Mm/LTAI.Mm.csproj` | 15 | 项目文件 |
| **合计** | **~1500** | |

---

## 8. 对比：上游 mm-cs vs LTAI.Mm

| 维度 | 上游 mm-cs | LTAI.Mm |
|------|-----------|---------|
| 总代码行 | ~8000+ | ~1500 |
| 支持语言 | 10 (Go/C#/TS/Py/Kt/Rs/Swift/Php/C/C++) | 1 (C#) |
| 代码生成 | CLI 工具 `mm -generate` | 无（不需要） |
| YAML/TOML | 计划中 | 不做（LTAI 已通过 MAF 处理 YAML） |
| 依赖 | 无 | 无 |
| JSONC 解析 | 完整解析器 | 简化版：仅提取 `// mm:` 标签 |
| 性能优化 | 通用 | 针对 LTAI 场景（session/Tool DTO） |
| 包体积 | 64KB | ~20KB |

---

## 9. 测试策略

- **单元测试**：每个编码器/解码器方法对应 5+ 测试用例（正常值、边界、异常）
- **fixture 兼容性**：使用上游 mm-cs 的 JSONC fixtures `tests/fixtures/` 验证编解码结果一致
- **集成测试**：Session 序列化前后对象相等
- **基准测试**：编码/解码速度 vs System.Text.Json（benchmark 门：MM 解码 ≤ JSON 解码 2x）

```csharp
// 典型 fixture 测试
[Fact]
public void EncodeDecode_Int64_Roundtrips() {
    var enc = new WireEncoder();
    enc.EncodeInt64(long.MaxValue);
    var dec = new WireDecoder(enc.ToByteArray());
    Assert.Equal(long.MaxValue, dec.Decode().As<long>());
}
```
