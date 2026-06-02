# LTAI Operations Runbook

## Prerequisites

- .NET 10.0 SDK
- Docker (for container deployment)
- DeepSeek API key (set as `DEEPSEEK_API_KEY` env var)

## Quick Start

```bash
# Set API key (required)
set DEEPSEEK_API_KEY=sk-your-key-here

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
| `DEEPSEEK_API_KEY` | L1 (flash) and L2 (pro) model API key |

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
      "DefaultProvider": "deepseek",
      "Model": "deepseek-v4-flash",
      "MaxTokens": 4096,
      "Temperature": 0.7,
      "Providers": {
        "deepseek-fast": { "Endpoint": "https://api.deepseek.com/v1", "Model": "deepseek-v4-flash" },
        "deepseek-pro": { "Endpoint": "https://api.deepseek.com/v1", "Model": "deepseek-v4-pro" }
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

Configured in `AIConfig.DegradationChain`:
```
deepseek → deepseek-pro → (end)
```

The router tries providers in order, skipping those in cooldown.

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

## Monitoring

- OpenTelemetry traces/metrics emit to console by default
- Configure OTLP endpoint via `LTAI_OTLP_ENDPOINT` env var
- Per-agent OTel source name: `LTAI.{AgentName}`
- DevUI dashboard: `GET /devui` (development only)
