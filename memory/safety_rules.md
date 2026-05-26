# memory: safety_rules
domain: safety
confidence: 0.90
version: 1.0.0

## summary
Safety boundaries and security rules enforced by the LTAI framework.

## facts
- workspace_boundary: NEVER modify files outside workspace root (LTAI_WORKSPACE env var) (confidence: 0.95)
- shell_consent: NEVER execute shell commands without user consent (onPreToolUse hook) (confidence: 0.90)
- secrets_protection: NEVER expose API keys, tokens, or secrets in output or commit messages (confidence: 0.95)
- file_deletion: NEVER delete user files without explicit confirmation (confidence: 0.90)
- internal_ips: No HTTP requests to internal IPs (10.x, 172.16-31.x, 192.168.x, 127.x) (confidence: 0.90)
- circuit_breaker: 5 consecutive failures triggers 30s cooldown (confidence: 0.85)
- retry: Tool call failures retry with exponential backoff (max 3) (confidence: 0.85)
- service_whitelist: ServiceDispatcher only allows 19 whitelisted types, blocks System.IO/Diagnostics/Net/Reflection namespaces (confidence: 0.90)

## context
These rules are defined in AGENTS.md, enhanced by AgentHookPipeline (shell/file/network safety) and enforced by DNA/Safety rule engine.

## tags
- safety
- security
- rules
- boundaries

## triggers
- pattern: "safety rule" (weight: 1.0)
- pattern: "security" (weight: 0.9)
- pattern: "boundary" (weight: 0.8)
