# skill: eia_air_quality
domain: eia/air
layer: 3
version: 1.0.0
intent: 大气环境影响评价 — 气象数据、污染源清单、AERMOD预测
triggers:
  - pattern: "大气环境|空气质量|废气.*评价|烟气.*评价"
    weight: 1.0
  - pattern: "大气.*影响|空气.*污染|排放.*标准|AERMOD"
    weight: 0.95
requires:
  - chinese_entity_extraction
confidence: 0.85

## 步骤
1. 确定评价等级（根据Pmax和D10%判定）
2. 收集气象数据（风速、风向、稳定度、温度）
3. 列出污染源参数（排气筒高度、内径、排放速率）
4. 选择预测模型（AERMOD/ADMS/CALPUFF）
5. 计算最大落地浓度和占标率
6. 预测对各敏感点的影响
7. 提出大气污染防治措施

## 验证
- must_contain: "评价等级"
- must_contain: "环境空气质量标准"
- must_contain: "占标率"
- pattern: "(?:SO2|NO2|PM10|PM2\\.5|TSP|VOCs)"
