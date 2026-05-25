# skill: eia_solid_waste
domain: eia/solid_waste
layer: 3
version: 1.0.0
intent: 固体废物环境影响评价 — 分类、产生量预测、处置措施
triggers:
  - pattern: "固体废物|固废.*评价|垃圾.*评价|危废"
    weight: 1.0
  - pattern: "固废.*处置|废物.*分类|危险废物|一般固废"
    weight: 0.95
requires: []
confidence: 0.84

## 步骤
1. 分类：一般工业固废 / 危险废物 / 生活垃圾
2. 预测产生量（产污系数法或类比法）
3. 判定危险特性（腐蚀性、毒性、易燃性、反应性）
4. 评价临时贮存场所的合规性
5. 分析处置方式（综合利用、填埋、焚烧）
6. 提出固废管理措施

## 验证
- must_contain: "固体废物"
- must_contain: "产生量"
- pattern: "(?:t/a|kg/d|吨/年)"
- pattern: "(?:危险废物|一般固废|生活垃圾)"
