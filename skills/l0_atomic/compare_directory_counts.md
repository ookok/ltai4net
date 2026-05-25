# skill: compare_directory_counts
domain: filesystem/compare
layer: 0
version: 1.0.0
intent: 对比两个目录的文件数量
triggers:
  - pattern: "对比|比较|哪个.*多|哪个.*少|各有多少|计数|统计.*文件"
    weight: 1.0
requires: []

## 步骤
1. shell: 分别列出两个目录的文件数
2. 计算差异并输出对比结果

## 验证
- must_contain: ""
