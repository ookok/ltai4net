# skill: list_processes
domain: system/process
layer: 0
version: 1.0.0
intent: 列出当前运行中的进程
triggers:
  - pattern: "\\b(?:进程|process)\\b|运行.*程序|running.*process|ps\\s"
    weight: 1.0
requires: []

## 步骤
1. shell: tasklist 或 ps aux

## 验证
- must_contain: ""
