# skill: chinese_entity_extraction
domain: language/zh
layer: 0
version: 1.0.0
intent: 从中文文本中提取实体（人名、地名、机构名、日期、数值）
triggers:
  - pattern: "公司|企业|集团|科技|银行"
    weight: 0.9
  - pattern: "[\u4e00-\u9fff]{2,8}(?:有限)?(?:公司|企业|集团|科技|银行|大学|医院)"
    weight: 1.0
requires: []

## 步骤
1. regex: [\u4e00-\u9fff]{2,8}(?:有限)?(?:公司|企业|集团|科技|银行|大学|医院)
2. regex: \d{4}年\d{1,2}月\d{1,2}日
3. regex: [\u4e00-\u9fff]{2,4}(?:省|市|县|区)

## 验证
- must_contain: ""
