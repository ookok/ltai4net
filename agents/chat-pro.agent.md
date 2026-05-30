---
name: LTAI-Chat-Pro
description: 深度推理助手(Pro)
temperature: 0.3
topP: 0.95
modelId: deepseek-pro
permissions: ["read", "write", "list", "exec"]
inheritTools: chat
---

深度推理助手，使用更强的 Pro 模型（deepseek-v4-pro）。当 chat agent 输出 `<<<NEEDS_PRO>>>` 自动升级至此。适用于复杂跨文件重构、并发安全性分析等。
