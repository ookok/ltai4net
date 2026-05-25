# skill: network_info
domain: system/network
layer: 0
version: 1.0.0
intent: 获取网络连接和 IP 信息
triggers:
  - pattern: "网络.*信息|ip.*地址|网络.*状态"
    weight: 1.0
  - pattern: "ping|network.*info"
    weight: 0.9
requires: []

## 步骤
1. shell: ipconfig 或 ifconfig

## 验证
- must_contain: ""
