# skill: read_file_content
domain: filesystem/read
layer: 0
version: 1.0.0
intent: 读取指定文件的内容
triggers:
  - pattern: "(?:读取|查看|读一下|打开|看看|显示).*(?:文件|内容|代码)"
    weight: 1.0
  - pattern: "(?:cat|type|more) .*\\.\\w+"
    weight: 0.95
requires: []

## 步骤
1. filesystem_read: 读取指定路径的文件内容

## 验证
- must_contain: ""
