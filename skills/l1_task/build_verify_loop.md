# skill: build_verify_loop
domain: code/build
layer: 1
version: 1.0.0
intent: 代码修改后自动编译并验证
triggers:
  - pattern: "build|编译|编译检查|dotnet build"
    weight: 1.0
requires:
  - "dotnet"
confidence: 0.95

## 步骤
1. shell: dotnet build
2. 检查输出中是否有 "error" 或 "错误"

## 验证
- must_not_contain: "失败"
- pattern: "已成功生成"
