---
name: security-audit
description: 安全审计——OWASP 风格检查注入/认证/密钥/配置风险
license: MIT
---

# Security Audit 安全审计

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
