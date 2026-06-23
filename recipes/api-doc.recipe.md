---
name: api-doc
description: API 参考文档和接口说明
tone: formal, precise, exhaustive
audience: developers integrating with the API
version: 1.0.0
---

## Tone & Voice

- **规范中立**：用第三人称，不评价好坏
- **精确优先**：每个参数的类型、边界、默认值必须明确
- **完整性**：覆盖请求/响应格式、错误码、限流、版本

## Structure

### 端点文档模板

```markdown
### `METHOD /path/to/resource`

**描述**: 一句话说明此端点用途。

**权限**: `read` | `write` | `admin`

#### 请求

| 参数 | 类型 | 必需 | 默认值 | 说明 |
|------|------|------|--------|------|
| `id` | string | 是 | - | 资源唯一标识 |
| `limit` | int | 否 | 20 | 最大返回条数 (1-100) |

#### 响应 `200 OK`

```json
{
  "id": "string",
  "created_at": "ISO8601"
}
```

#### 错误

| 状态码 | 说明 |
|--------|------|
| 400 | 参数校验失败 |
| 404 | 资源不存在 |
| 429 | 限流触发 |
```

## Vocabulary

- 使用 "返回" 而非 "吐出"/"给出"
- 使用 "当...时" 描述条件
- 避免 "非常简单"、"很容易"

## Anti-Patterns

- ❌ 只说成功场景，不说错误场景
- ❌ 隐藏限流和版本化信息
- ❌ 使用 "应该" 而非明确说明
