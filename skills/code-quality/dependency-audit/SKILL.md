---
name: dependency-audit
description: 依赖审计——检查过期包/已知漏洞/冗余依赖/许可合规
license: MIT
---

# Dependency Audit 依赖审计

审查项目的 NuGet 依赖健康状况。

## 1. 版本过期
- 对比当前版本和最新稳定版
- 关注大版本升级（major）的 breaking changes
- 预览版（preview/rc）是否可升级到稳定版

## 2. 已知漏洞
- 检查是否有已知 CVE
- 关注 GitHub Advisory Database 标记
- 高/严重漏洞优先修复

## 3. 冗余依赖
- 是否有未使用的包引用？
- 是否有重复功能的包（如两个 JSON 库）？
- 是否可被 .NET 内置功能替代？

## 4. 许可合规
- 检查包许可证类型
- GPL/AGPL 类许可证是否与项目兼容
- 商业使用是否需要额外授权

## 5. 建议输出格式

```
## Dependency Audit Report

### 🔴 高危
- PackageA v1.0 → v2.0 (CVE-2024-xxx)

### 🟡 建议更新
- PackageB v3.2 → v3.5

### 🟢 健康
- PackageC v5.0 ✅
```
