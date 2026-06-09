---
name: security-audit
description: 安全审计——OWASP 风格检查注入/认证/密钥/配置风险
license: MIT
allowedTools: [ReadFileContent, SearchContent, FindInCode, Glob, DirectoryTree]
---

# Security Audit 安全审计

## 概要
- 六维安全审计：注入风险、认证鉴权、密钥泄漏、配置安全、XSS/CSRF、供应链
- 工具配合：SearchContent 搜索模式、FindInCode 定位关键 API、Glob 圈定审计范围
- 输出：问题摘要 + 文件位置 + 修复建议
- 范围：所有 .cs .js .ts .py 文件及相关配置

对代码库进行安全审计，按以下维度逐一检查：

## 1. 注入风险
- SQL 注入：参数化查询 vs 字符串拼接
- 命令注入：`Process.Start` 参数是否转义
- 路径遍历：用户输入是否经 `Path.GetFullPath` + 前缀验证

## 2. 密钥管理
- API Key 是否硬编码在源码中
- 连接字符串是否包含密码
- 是否使用了 `SecretManager` 统一管理密钥

## 3. 认证与授权
- API 端点是否需要身份验证
- 是否有缺失的权限检查
- Token/会话管理是否安全

## 4. 数据保护
- 敏感数据是否加密存储
- HTTPS 是否强制使用
- 日志中是否可能泄露敏感信息

## 5. 辅助工具

| 检查项 | 推荐工具 |
|--------|---------|
| 搜索硬编码密码/密钥 | `SearchContent(pattern: "password|api_key|secret|token\\s*=", glob: "*.cs")` |
| 搜索 SQL 拼接 | `SearchContent(pattern: "(Select|Insert|Update|Delete)\\s+.*\\+\\s", glob: "*.cs")` |
| 搜索命令注入 | `SearchContent(pattern: "Process\\.Start|ShellExecute", glob: "*.cs")` |
| 搜索 `[Obsolete]` 不安全 API | `FindInCode(name: "Obsolete", kind: "any")` |
| 检查密钥管理 | `SearchContent(pattern: "SecretManager\\.(Get|Set)", glob: "*.cs")` |
