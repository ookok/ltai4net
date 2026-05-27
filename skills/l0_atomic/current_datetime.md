# skill: current_datetime
domain: system/datetime
layer: 0
version: 1.1.0
intent: 获取当前日期和时间
triggers:
  - pattern: "^(?:现在|当前).*(?:时间|日期|几点)"
    weight: 1.0
  - pattern: "what.*(?:time|date).*now|current.*(?:time|date)"
    weight: 1.0
requires: []

## 步骤
1. shell: date

## 验证
- must_contain: ""
