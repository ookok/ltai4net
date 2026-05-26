# memory: eia_standards
domain: eia
confidence: 0.85
version: 1.0.0

## summary
Chinese environmental standards (GB/HJ) commonly referenced in EIA assessments.

## facts
- GB3095-2012: Ambient air quality standards — SO2, NO2, PM10, PM2.5, CO, O3 limits by class I/II (confidence: 0.90)
- GB3838-2002: Surface water quality standards — COD, BOD, DO, NH3-N, TP, TN limits by class I-V (confidence: 0.90)
- GB3096-2008: Environmental noise standards — daytime/night limits by zone category 0-4 (confidence: 0.90)
- HJ2.2-2018: Technical guidelines for atmospheric environmental impact assessment — AERMOD, AERSCREEN methods (confidence: 0.85)
- GB/T3840-1991: Technical methods for local air pollutant emission standards — Gaussian plume model basis (confidence: 0.85)
- skill_coverage: 21 EIA tools available as .md files covering air/water/noise/soil/carbon models (confidence: 0.95)

## context
EIA domain skills are at L3 layer in skills/l3_domain/. Tools are at tools/eia/ as .md files with Service type configuration. Models use static computation methods in LTAIToolRegistry.

## tags
- eia
- standards
- environment
- regulations

## triggers
- pattern: "environmental standard" (weight: 1.0)
- pattern: "GB standard" (weight: 0.9)
- pattern: "air quality standard" (weight: 0.9)
- pattern: "water standard" (weight: 0.9)
