# option: model_routing
section: LTAI:AI:Routing
description: Unified provider configuration. L0 merged into L1 (fast). Only provider + mode needed; models auto-derived.

## keys
- routing.provider: string (default: deepseek) — Unified provider name; one provider handles fast inference + deep reasoning + embedding
  env: LTAI_PROVIDER
  note: Replaces routing.l0.*, routing.l1.*, routing.l2.* — those are now deprecated.
- routing.mode: string (default: balanced) — Selection mode: fast (cheapest), balanced (default), quality (deepest)
  env: LTAI_MODE
  options: [fast, balanced, quality]
- routing.max_tokens: int (default: 4096) — Max response tokens
  env: LTAI_MAX_TOKENS
- routing.temperature: float (default: 0.3) — Default generation temperature
  env: LTAI_TEMPERATURE
- routing.daily_budget_usd: decimal (default: 10.00) — Daily usage budget in USD
  env: LTAI_DAILY_BUDGET
- routing.circuit_breaker_failures: int (default: 5) — Consecutive failures before circuit break
  env: LTAI_CIRCUIT_BREAKER
- routing.circuit_breaker_cooldown_sec: int (default: 30) — Cooldown seconds after circuit break
  env: LTAI_CIRCUIT_COOLDOWN
- routing.timeout_ms: int (default: 60000) — Request timeout in milliseconds
  env: LTAI_TIMEOUT_MS
## tags
- model
- routing
- unified
