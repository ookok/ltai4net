# skill: current_directory
domain: filesystem/cwd
layer: 0
version: 1.0.0
intent: 显示当前工作目录
triggers:
  - pattern: "当前.*目录|工作.*目录|在.*哪个.*目录|在.*什么.*目录"
    weight: 1.0
  - pattern: "pwd|working.*dir|cwd"
    weight: 0.95
requires: []

## 步骤
1. shell: pwd 或 cd

## 验证
- must_contain: ""
