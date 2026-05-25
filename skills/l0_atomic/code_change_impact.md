# skill: code_change_impact
domain: code/impact
layer: 0
version: 1.0.0
intent: 分析代码变更的影响范围
triggers:
  - pattern: "理解.*(?:代码|变更|diff)|分析.*影响|影响.*分析"
    weight: 0.9
  - pattern: "understand.*(?:code|diff)|analyze.*impact"
    weight: 0.85
requires:
  - git_diff_analysis
confidence: 0.85

## 步骤
1. → git_diff_analysis
2. 分析变更文件的依赖关系
3. 评估影响范围

## 验证
- must_contain: "影响"
