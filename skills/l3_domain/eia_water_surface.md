# skill: eia_water_surface
domain: eia/water/surface
layer: 3
version: 1.0.0
intent: 地表水环境影响评价 — 水质现状监测、标准比对、影响预测
triggers:
  - pattern: "地表水|地面水|河流.*评价|湖水.*评价|水库.*评价"
    weight: 1.0
  - pattern: "水环境.*现状|水质.*监测|水质.*标准"
    weight: 0.95
requires:
  - chinese_entity_extraction
confidence: 0.88

## 步骤
1. 确定评价等级（根据污水排放量和受纳水体规模）
2. 收集现状监测数据（pH、COD、BOD5、NH3-N、TP等）
3. → chinese_entity_extraction 提取水体名称和行政区划
4. 单因子指数法评价现状水质
5. 选择预测模型（S-P模式/二维稳态混合模式）
6. 预测施工期和运营期对水质的影响
7. 提出水环境保护措施

## 验证
- must_contain: "评价等级"
- must_contain: "地表水环境质量标准"
- must_contain: "防治措施"
- pattern: "(?:pH|COD|BOD|氨氮|总磷|溶解氧)"
- pattern: "[\u4e00-\u9fff]+(?:河|湖|江|水库|海域)"
