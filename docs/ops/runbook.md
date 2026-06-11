# LTAI Operations Runbook

## Prerequisites

- .NET 10.0 SDK
- Docker (for container deployment)
- At least one API key (DeepSeek, OpenAI, etc.)

## Quick Start

```bash
# Set API key (at least one required)
set DEEPSEEK_API_KEY=sk-your-key-here
# L2/L3 models auto-selected; no further config needed

# Run TUI (interactive console)
cd src/LTAI.TUI && dotnet run

# Run Desktop (Avalonia GUI)
cd src/LTAI.Desktop && dotnet run

# Run Web server
cd src/LTAI.Web && dotnet run
# → http://localhost:5100

# Docker deployment
docker build -t ltai-web -f Dockerfile .
docker run -p 5100:5100 -e DEEPSEEK_API_KEY=sk-xxx ltai-web
```

## Configuration

All config lives in `appsettings.json` under the `LTAI` section.

### Required env vars

| Variable | Description |
|---|---|
| `DEEPSEEK_API_KEY` | DeepSeek L1 model (auto-selects L2/L3) |
| `OPENAI_API_KEY` | OpenAI fallback |
| (Any provider's API key works — only one needed) |

### Optional env vars

| Variable | Description |
|---|---|
| `LTAI_DATA_DIR` | Override data directory (default: `<BaseDir>/.livingtree`) |
| `LTAI_OTLP_ENDPOINT` | OTLP exporter endpoint (e.g., `http://jaeger:4317`) |

### Key config sections

```json
{
  "LTAI": {
    "AI": {
      "DefaultProvider": "deepseek-fast",
      "L1": { "Provider": "deepseek-fast", "Model": "deepseek-chat" },
      "AutoSelect": {
        "Enabled": true,
        "RefreshIntervalMin": 30
      }
    }
  }
}
```

## Health Checks

| Endpoint | Purpose | Expected Response |
|---|---|---|
| `GET /health` | Full health check (KG + LLM providers) | `{"status":"healthy", "checks":[...]}` |
| `GET /ready` | Kubernetes readiness probe | `{"status":"ready"}` or 503 |

## Circuit Breaker

Provider failures are tracked in SQLite (`.livingtree/circuit_breaker.db`):
- **3 consecutive failures** → 30-second cooldown
- Auth errors (401/403) → permanent ban for session
- Rate limiting (429) → honors Retry-After header
- Successful call → resets failure count

## Degradation Chain

The router degrades through L1 → L1-alt → L2 → L2-alt → L3:
```
deepseek-chat → (no alt) → deepseek-reasoner → (no alt) → deepseek-chat (reuse L1)
```

The chain is auto-configured by ModelAutoSelector based on ProviderRegistry data.

## Model Auto-Selection

At startup, `ModelAutoSelectHostedService` runs and:
1. Reads `ProviderRegistry` (8 providers × 560+ models from `models/models-dev-providers.json`)
2. Scores each model: capability(40%) + cost(30%) + speed(20%) + availability(10%)
3. Assigns L1 (fast), L2 (deep), L3 (judge) tiers
4. L3 falls back to L1 if no suitable model found

CLI commands:
```bash
ltai models show                  # Current selections
ltai models set l2 gpt-4o-mini   # Override a tier
ltai models auto l2               # Restore auto-selection
```

## Troubleshooting

### Agent fails to build

Each agent is independently isolated. A failure in one agent does not block others.
A `FallbackAgent` returns an error message instead of crashing the process.
Check startup logs for `Agent '{Name}' failed to build`.

### ONNX model not available

```bash
# Download models manually
cd src/LTAI.AI && dotnet build -t:DownloadEmbeddingModelMiniLM
dotnet build -t:DownloadEmbeddingModelBgeSmallZh
dotnet build -t:DownloadEmbeddingModelBgeSmallEn
```

### Reset persistence

```bash
# Clear all persistent state
rm -rf .livingtree/
```

### Provider data not loading

```bash
# The fallback dataset is at models/models-dev-providers.json (252KB)
# Delete it to trigger a fresh fetch from https://models.dev/api.json
rm models/models-dev-providers.json
```

## Monitoring

- OpenTelemetry traces/metrics emit to console by default
- Configure OTLP endpoint via `LTAI_OTLP_ENDPOINT` env var
- Per-agent OTel source name: `LTAI.{AgentName}`
- DevUI dashboard: `GET /devui` (development only)
