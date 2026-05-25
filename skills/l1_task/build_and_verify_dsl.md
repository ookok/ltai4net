# skill: build_and_verify_dsl
domain: code/build
layer: 1
version: 2.0.0
intent: 编译项目并自动分析错误，使用 DSL 变量和分支
triggers:
  - pattern: "build.*verify|编译.*验证|dotnet build"
    weight: 1.0
requires: []

## 步骤
1. shell: dotnet build → $build_result
2. regex: error CS\d+.* from $build_result → $errors
3. 编译结果: {{ $errors.count }} 个错误

## 分支 when $errors.count == 0
1. 输出: "编译通过，零错误 ✓"
2. shell: dotnet test --no-build → $test_result

## 分支 when $errors.count > 0
1. 输出: "发现 {{ $errors.count }} 个编译错误，修复后重试"
2. 提取第一个错误: {{ $errors[0].g1 }}

before_each:
  - 记录开始步骤执行

after_each:
  - 记录步骤完成

on_error:
  - 记录错误: $_error
  - 如果 {{ $_retry_count < 3 }} 则重试

## 验证
- must_contain: "编译"
