# skill: execute_shell_command
domain: shell/exec
layer: 0
version: 1.0.0
intent: 执行 Shell 命令并返回结果
triggers:
  - pattern: "(?:执行|运行|跑一下|运行一下).*(?:命令|脚本|构建|测试)"
    weight: 1.0
  - pattern: "dotnet (?:build|test|run|publish)"
    weight: 1.0
  - pattern: "npm (?:install|run|test|build)"
    weight: 1.0
  - pattern: "git (?:clone|pull|push|commit|diff|log)"
    weight: 1.0
requires: []

## 步骤
1. shell: 执行对应的 Shell 命令

## 验证
- must_not_contain: "Error"
