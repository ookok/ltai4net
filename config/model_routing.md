# option: model_routing
section: LTAI:AI:Routing
description: Model tier assignments and degradation chain. L0=local(ONNX/GGUF), L1=fast cloud, L2=deep cloud. Degradation flows L2→L1→L0 on budget/circuit-break.

## keys
- routing.l0.provider: string (default: onnx) — L0 provider name (local inference)
  env: LTAI_L0_PROVIDER
- routing.l0.model: string (default: model.onnx) — L0 model file or ID
  env: LTAI_L0_MODEL
- routing.l1.provider: string (default: deepseek-fast) — L1 fast cloud provider
  env: LTAI_L1_PROVIDER
- routing.l1.model: string (default: deepseek-v4-flash) — L1 fast model
  env: LTAI_L1_MODEL
- routing.l2.provider: string (default: deepseek) — L2 deep cloud provider
  env: LTAI_L2_PROVIDER
- routing.l2.model: string (default: deepseek-v4-pro) — L2 deep model
  env: LTAI_L2_MODEL
- routing.default.provider: string (default: deepseek) — Default provider for general queries
  env: LTAI_DEFAULT_PROVIDER
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
- tier
- degradation
