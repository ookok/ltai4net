# 语义代码块索引（cocoindex 启发）

## Goal

让 LLM 理解代码时不再是 `ReadFileContent` 读整文件，而是预索引的语义代码块直接成为可用上下文。目标：代码类问答节省 50-70% token。

## 现有资产（不重复造轮子）

| 组件 | 文件 | 现有能力 |
|------|------|---------|
| KgStore | `Vector/KgStore.cs` | SQLite + FTS5 + VecNodes(384d BLOB) + HNSW + WAL |
| TreeSitterParser | `Tools/TreeSitterParser.cs` | 13 语言 AST 解析，`ExtractSymbols` 返回 (kind, name, line, col) |
| EmbeddingClient | `AI/EmbeddingClient.cs` | ONNX 嵌入推理 + FastEmb BM25 fallback |
| DirectoryWalker | `Utils/DirectoryWalker.cs` | BFS 文件遍历 |
| AIContextProvider | `extern` 基类 | Pipeline 注入 Instructions/Messages/Tools |
| SemanticChunker | `Indexing/SemanticChunker.cs` | 段落/句子边界分块（用于文档，非代码） |

## 新增组件（~350 行）

### 1. `CodeChunkIndex` (AIContextProvider) — ~200 行

**目的**：在 `CgGraph` 建好代码图后，额外创建语义代码块索引。

```
AST 文件 → tree-sitter 解析 → 提取函数/方法/类体 → 按语义边界切片 → 嵌入 → 存 KgStore
```

**索引时机**：`BuildAsync` 时同时建立（与 CgGraph 共享文件扫描结果）。

**分块策略**：
- 对每个文件，`_parser.Parse(code, ext)` 获取 AST
- 遍历顶层声明节点：`class_declaration`，`method_declaration`，`function_definition`，`interface_declaration` 等
- 对每个声明节点：取 `StartPosition.Row+1` 到 `EndPosition.Row+1` 的代码行作为 chunk
- 附加 metadata：`(file_path, language, start_line, end_line, kind, name)`
- 嵌入 chunk 文本 → 存 `VecNodes`（复用现有表，node 的 kind="chunk"）

**存储**：
- 在 `KgStore.Nodes` 中创建 kind="chunk" 的节点
- `Docs.text` = chunk 源代码
- `VecNodes.vec` = chunk 嵌入 (384d)
- `source` = `"file.cs:L42-L78"`
- 通过边关联到文件节点 (`Edges(src=fileNode, dst=chunkNode, rel="contains")`)

**查询**：
```csharp
public async Task<string> QueryAsync(string query, int topK = 5, CancellationToken ct = default)
{
    var qvec = await _embedder.GenerateAsync(query, ct);
    var hits = await _store.SearchVector(qvec, topK, kindFilter: "chunk");
    // 返回 Markdown: 📄 auth.py:42-78  score=0.87
    //             ```python
    //             def create_session(...): ...
    //             ```
}
```

### 2. LLM 工具 `SemanticCodeSearch` — ~100 行

暴露为 `AITool`，agent 可直接调用：

```csharp
[Description("语义搜索代码库。输入自然语言描述，返回最匹配的代码片段。" +
    "优先于 ReadFileContent：先用此工具理解代码结构和逻辑，需要完整文件时才用 ReadFileContent。")]
public async Task<string> SemanticCodeSearch(
    [Description("自然语言查询，如 'how are sessions created'")] string query,
    [Description("文件类型过滤，如 '*.cs'")] string glob = "*",
    [Description("返回结果数量")] int limit = 5)
```

### 3. 系统提示更新

在 `BuildSystemPrompt()` 中加一条规则：

```
## 代码理解
当需要理解代码时，先调用 SemanticCodeSearch 获取相关代码片段。
只有片段不够用时（如需要编辑、查看完整结构）才调用 ReadFileContent。
```

## 集成位置

### AIContextProviders 链

在 `kbGraph`、`codeGraph` 后、`wasmtimeSandbox` 前插入 `CodeChunkIndex`：

```
// [7] kbGraph → [8] codeGraph → [8a] CodeChunkIndex → [9] wasmtimeSandbox
```

### 工具注册

对所有 `canRead` 的 agent 注册 `SemanticCodeSearch`（紧挨 `ReadFileContent` 注册处）：

```csharp
if (canRead) tools.Add(AIFunctionFactory.Create(codeChunkIndex.SemanticCodeSearch));
```

## 索引时机与增量

- `CodeChunkIndex` 与 `CgGraph` 共享 `_indexedFiles` 缓存
- `BuildAsync` 时同步构建 chunk 索引
- 修改过的文件重索引时删除旧的 chunk 节点（通过 `source` 匹配）再重建

## 索引格式示例

```
📄 src/auth/login.py:42-78  score=0.87
```python
def create_session(user_id: int, ttl: int = 3600) -> Session:
    session = Session(user_id=user_id, expires_at=now() + ttl)
    db.add(session)
    db.commit()
    return session
```

📄 src/auth/login.py:120-135  score=0.72
```python
class SessionManager:
    def __init__(self, db: Database):
        self._db = db
        self._cache = {}
```

📄 src/api/handlers.py:15-30  score=0.65
```python
@app.post("/sessions")
async def create_session_handler(user_id: int):
    mgr = SessionManager(get_db())
    session = mgr.create(user_id)
    return {"session_id": session.id}
```

## 已知限制

- chunk 嵌入用 MiniLM (384d)，代码专用度不如 `nomic-ai/CodeRankEmbed`（768d）。4000 files × 20 chunks × 1.5KB = ~120MB 索引，可接受
- tree-sitter 覆盖 13 种语言；未覆盖的语言退化到固定行数分块（每 30 行一切）
- HNSW 建在内存中（`KgStore` 启动时从 `VecNodes` 重建），~200ms/10万向量
- 长函数（>200 行）不退化为子 chunk — 当前策略：原样保留，靠 RRF 排序兜底
