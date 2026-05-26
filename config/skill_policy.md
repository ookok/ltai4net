# option: skill_policy
description: Skill auto-evolution and promotion thresholds

## keys
- min_successes_for_l0: int (default: 3) — Minimum successes to auto-create L0 skill
- min_successes_for_l1: int (default: 5) — Minimum successes to qualify for L1
- min_successes_for_l2: int (default: 10) — Minimum successes to qualify for L2
- suggestion_l1_uses: int (default: 3) — Suggest L1 when total uses below this
- suggestion_l2_uses: int (default: 10) — Suggest L1 at uses below this
- suggestion_l2_rate: float (default: 0.7) — Success rate threshold for L2 promotion
- suggestion_l3_uses: int (default: 50) — Uses threshold for L3 promotion
- suggestion_l3_rate: float (default: 0.85) — Success rate threshold for L3 promotion

## tags
- skill
- evolution
- policy
