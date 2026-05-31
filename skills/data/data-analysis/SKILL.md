---
name: data-analysis
description: 数据分析——结构化数据探索/统计摘要/可视化方案/异常检测
license: MIT
---

# Data Analysis 数据分析

对给定的数据集进行结构化分析。

## 1. 数据导入
- 从 Excel 文件读取：`ExcelRead(path, sheet, range)` → 获取表格数据
- 从 CSV/文本文件读取：`ReadFileContent(path)` → 获取原始文本
- 从数据库读取：通过 `RunCommand` 执行 SQL 查询

## 2. 数据概况
- 数据量：行数、列数
- 数据类型：数值/类别/时间/文本
- 缺失值：各列缺失比例
- 统计摘要：均值/中位数/标准差/分位数

## 2. 探索性分析
- 分布：直方图（数值）、柱状图（类别）
- 相关性：热力图、散点矩阵
- 趋势：时间序列折线图
- 异常：箱线图标记离群点

## 3. 可视化建议

| 目的 | 图表类型 | 适用数据 |
|------|---------|---------|
| 分布 | 直方图 / KDE | 数值 |
| 对比 | 柱状图 / 箱线图 | 类别 vs 数值 |
| 趋势 | 折线图 | 时间序列 |
| 关系 | 散点图 / 热力图 | 数值 vs 数值 |
| 占比 | 饼图 / 环形图 | 类别占比 |
| 层级 | 树图 / 旭日图 | 分层数据 |

## 4. 异常检测
- 统计方法：Z-score > 3 / IQR 法则
- 时序异常：移动平均偏差
- 业务规则：超阈值标记

## 5. 辅助工具

| 场景 | 推荐工具 |
|------|---------|
| 读取 Excel 数据 | `ExcelRead(path, sheet, range)` |
| 导出结果为 Excel | `ExcelWrite(path, cellsJson, create: true)` |
| 搜索数据文件 | `Glob(pattern: "*.xlsx")` / `Glob(pattern: "*.csv")` |
| Web 数据采集 | `WebFetch(url)` |
| 复杂数据管道 | `WorkflowSequential(agentNames: "data,writer", task)` |
