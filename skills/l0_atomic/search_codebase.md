# skill: search_codebase
domain: code/search
layer: 0
version: 1.0.0
intent: 在代码库中搜索函数、类或关键字的定义和引用
triggers:
  - pattern: "(?:搜索|查找|找一下|搜索一下).*(?:代码|函数|类|文件|方法|定义)"
    weight: 1.0
  - pattern: "(?:哪里|何处|什么地方).*(?:定义|使用|调用)"
    weight: 0.9
requires: []

## 步骤
1. grep: 搜索关键字在 .cs/.ts/.py 等源文件中

## 验证
- must_contain: ""
