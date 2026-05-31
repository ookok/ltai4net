---
name: api-design
description: API 设计审查——RESTful 路由/状态码/请求响应模型/版本控制
license: MIT
---

# API Design Review

审查 API 设计是否符合 RESTful 最佳实践。

## 1. 路由设计
- 资源名使用复数：`/users` 而非 `/user`
- 嵌套不超过 2 层：`/users/{id}/orders`
- 操作用 HTTP 动词表达，不在 URL 中：`POST /users` 而非 `POST /users/create`

## 2. 状态码
- `200 OK` — 查询成功
- `201 Created` — 创建成功
- `204 No Content` — 删除成功
- `400 Bad Request` — 参数校验失败
- `401 Unauthorized` — 未认证
- `403 Forbidden` — 无权限
- `404 Not Found` — 资源不存在
- `409 Conflict` — 数据冲突
- `422 Unprocessable` — 业务校验失败
- `429 Too Many Requests` — 限流

## 3. 请求响应模型
- 请求体使用 JSON，字段名 camelCase
- 响应包含统一包装：`{ data, meta, errors }`
- 分页：`?page=1&size=20` → `{ data, meta: { total, page, size } }`
- 错误格式：`{ errors: [{ code, message, field? }] }`

## 4. 版本控制
- 通过 URL 前缀或 Header 做版本控制
- 向后兼容：新增字段不删除旧字段
- 废弃用 `deprecated` 标记 + 迁移期
