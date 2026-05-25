# skill: skill_extractor
domain: meta/skill
layer: 4
version: 1.0.0
intent: 从成功对话中自动提炼新的 Skill
triggers:
  - pattern: "记住这个|记住这些|提炼技能|创建技能|create skill"
    weight: 1.0
requires: []
confidence: 0.75

## 步骤
1. 分析当前对话中成功的工具调用序列
2. 提取触发模式（用户说了什么触发了这个工具组合）
3. 提取验证规则（什么条件判定结果正确）
4. 确定领域和层级
5. 生成 skill.md 文件到对应层目录

## 验证
- must_contain: "## 步骤"
- must_contain: "triggers:"
