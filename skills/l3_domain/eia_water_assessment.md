# skill: eia_water_assessment
domain: eia/water
layer: 3
version: 1.0.0
intent: 地表水环境影响评价
triggers:
  - pattern: "水环境|地表水|水质|水污染|废水"
    weight: 1.0
  - pattern: "EIA.*水|环境影响.*水"
    weight: 0.9
  - pattern: "水环境影响评价"
    weight: 1.0
requires:
  - chinese_entity_extraction
confidence: 0.88

## 步骤
1. 确定评价等级（一/二/三级）
2. 收集现状监测数据
3. → chinese_entity_extraction 提取水系名称
4. 单因子指数法评价
5. 预测影响范围
6. 提出防治措施

## 验证
- must_contain: "评价等级"
- must_contain: "水质标准"
- must_contain: "防治措施"
- pattern: "[\u4e00-\u9fff]+(?:河|湖|江|水库|海域)"
