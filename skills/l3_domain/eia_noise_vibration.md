# skill: eia_noise_vibration
domain: eia/noise
layer: 3
version: 1.0.0
intent: 声环境影响评价 — 噪声监测、等声级线图、振动评价
triggers:
  - pattern: "噪声.*评价|声环境|振动.*评价|噪音"
    weight: 1.0
  - pattern: "噪声.*标准|噪声.*监测|隔声|降噪"
    weight: 0.9
requires: []
confidence: 0.82

## 步骤
1. 确定声环境功能区类别
2. 现场监测或类比获取背景噪声值
3. 预测施工期噪声（点声源衰减模式）
4. 预测运营期噪声（工业噪声预测模式）
5. 绘制等声级线图
6. 评价声环境保护目标达标情况
7. 提出降噪措施

## 验证
- must_contain: "声环境质量标准"
- must_contain: "dB(A)"
- pattern: "(?:昼间|夜间).*\\d+.*dB"
