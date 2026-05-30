---
name: doc-generator
description: 文档生成——XML 注释/README/API 文档/变更日志
license: MIT
---

# Doc Generator 文档生成

为指定代码生成规范的文档。

## 1. XML 注释
```csharp
/// <summary>
/// 方法/类的简短说明
/// </summary>
/// <param name="paramName">参数说明</param>
/// <returns>返回值说明</returns>
/// <exception cref="ExceptionType">异常条件</exception>
```

## 2. README 结构
```markdown
# 项目名
简短描述

## 功能特性
- ...

## 快速开始
### 前置条件
### 安装
### 使用

## API
### 方法/类说明

## 贡献指南
```

## 3. 变更日志
```markdown
## [1.1.0] - 2025-01-01
### Added
- 新功能
### Changed
- 变更内容
### Fixed
- 修复内容

## [1.0.0] - 2024-12-01
### Added
- 初始版本
```
