# skill: list_directory_contents
domain: filesystem/list
layer: 0
version: 1.0.0
intent: 列出目录中的文件和子目录
triggers:
  - pattern: "列出.*(?:文件|目录|内容|文件夹)"
    weight: 1.0
  - pattern: "显示.*(?:文件|目录)"
    weight: 0.9
  - pattern: "(?:有什么|有哪些).*文件"
    weight: 0.9
requires: []

## 步骤
1. shell: dir 或 ls，根据平台选择
2. 解析输出，区分文件和目录

## 验证
- must_contain: ""
