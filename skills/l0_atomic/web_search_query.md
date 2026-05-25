# skill: web_search_query
domain: web/search
layer: 0
version: 1.0.0
intent: 执行网络搜索获取实时信息
triggers:
  - pattern: "(?:搜索|查一下|百度一下|google|bing).*(?:一下|看看|关于)"
    weight: 1.0
  - pattern: "(?:最新|最近|今天|今年|实时).*(?:消息|新闻|动态|公告|数据)"
    weight: 0.95
  - pattern: "什么是|是什么意思|什么是.*意思"
    weight: 0.8
requires: []

## 步骤
1. web_search: 搜索用户查询中的关键词

## 验证
- must_contain: ""
