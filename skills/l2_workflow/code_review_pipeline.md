# skill: code_review_pipeline
domain: code/review
layer: 2
version: 1.0.0
intent: 完整代码审查流程：diff分析 → 静态检查 → 架构审查 → 安全扫描
triggers:
  - pattern: "代码审查|code review|review|审查代码"
    weight: 1.0
requires:
  - git_diff_analysis
  - build_verify_loop
confidence: 0.82

## 步骤
1. → git_diff_analysis
2. → build_verify_loop
3. 分析变更文件的架构影响
4. 检查安全问题（SQL注入、XSS、硬编码密钥）
5. 生成审查报告

## 验证
- must_contain: "审查报告"
- must_contain: "安全"
