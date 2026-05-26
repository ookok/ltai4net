# prompt: eia_data_processor
domain: eia
description: Environmental Impact Assessment data processing template

## template
Process the following environmental data using standard EIA methods:

Data: {{data}}
Standard: {{standard}}
Output format: {{output_format}}

Steps:
1. Validate input data against the standard's parameter ranges
2. Run the appropriate dispersion/attenuation/mixing model
3. Compare results against relevant Chinese GB/HJ standards
4. Generate assessment report with conclusions

## variables
- data: Environmental input data (required)
- standard: Applicable standard code e.g. GB3095-2012 (default: GB3095-2012)
- output_format: Desired output format (default: report)

## triggers
- pattern: "environmental assessment" (weight: 1.0)
- pattern: "air quality" (weight: 0.9)
- pattern: "water quality" (weight: 0.9)
- pattern: "noise" (weight: 0.8)
- pattern: "环境影响" (weight: 1.0)

## tags
- eia
- data
- processing
