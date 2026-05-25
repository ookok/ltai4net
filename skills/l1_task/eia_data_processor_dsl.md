# skill: eia_data_processor_dsl
domain: eia/water
layer: 1
version: 2.0.0
intent: 处理 EIA 水质监测数据，使用 DSL 变量和表达式
triggers:
  - pattern: "处理.*监测.*数据|分析.*水质.*数据|计算.*指数"
    weight: 0.9
requires: []

## 步骤
1. shell: echo "pH=7.2,COD=15.3,BOD5=4.1,NH3-N=0.8,TP=0.12" → $raw_data
2. regex: (\w[\w-]*)=([\d.]+) from $raw_data → $params
3. 计算: 提取了 {{ $params.count }} 个水质参数

## 分支 when $params.count >= 3
1. 输出: "数据完整，开始评价"
2. 输出: "pH: {{ $params[0].g1 }}={{ $params[0].g2 }}"

## 分支 when $params.count < 3
1. 输出: "监测数据不足，需要至少3项指标"

## 验证
- must_contain: "pH"
