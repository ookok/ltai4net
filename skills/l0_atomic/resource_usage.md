# skill: resource_usage
domain: system/monitor
layer: 0
version: 1.0.0
intent: 获取系统资源使用情况（CPU、内存、磁盘）
triggers:
  - pattern: "系统.*(?:负载|资源|CPU|内存|磁盘)|资源.*(?:使用|占用|情况)"
    weight: 1.0
  - pattern: "(?:cpu|memory|disk).*(?:usage|load|free|available)"
    weight: 0.95
requires: []

## 步骤
1. shell: wmic cpu get loadpercentage 2>nul || grep "cpu " /proc/stat
2. shell: wmic os get FreePhysicalMemory,TotalVisibleMemorySize 2>nul || free -m

## 验证
- must_contain: ""
