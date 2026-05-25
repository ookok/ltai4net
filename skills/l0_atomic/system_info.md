# skill: system_info
domain: system/env
layer: 0
version: 1.0.0
intent: 获取操作系统和环境信息
triggers:
  - pattern: "系统.*信息|环境.*信息|操作系统|系统.*配置|什么.*系统"
    weight: 1.0
  - pattern: "what.*(?:os|system|platform)"
    weight: 0.95
requires: []

## 步骤
1. shell: 获取系统信息（OS、CPU、内存）

## 验证
- must_contain: ""
