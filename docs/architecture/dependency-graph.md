# LTAI Dependency Graph

## Overview

LTAI is built on **Microsoft Agent Framework (MAF)** — the extern submodule at `extern/agent-framework/` (commit `edcc786`). MAF provides the AI agent abstractions, protocol layers, hosting, and workflow primitives. LTAI adds 80+ custom tools, WASM sandboxing, document processing, code analysis, and multi-agent orchestration.

## Git Submodules

| Submodule | Path | Pin | Description |
|---|---|---|---|
| agent-framework | `extern/agent-framework` | `edcc786` (main) | MAF source tree, 36 dotnet src projects |
| durabletask-dotnet | `extern/durabletask-dotnet` | `v1.24.2` | Durable Task Framework (DTFx), 6 src projects (shallow clone) |

Setup scripts: `scripts/dev-setup-submodules.ps1` (Windows), `scripts/dev-setup-submodules.sh` (Linux/macOS).

## DTFx Version Table

| Package | Version | Source | Used By |
|---|---|---|---|
| `Microsoft.DurableTask.Client` | 1.24.2 | MAF Directory.Packages.props | LTAI.DurableTask (transitive) |
| `Microsoft.DurableTask.Worker` | 1.24.2 | MAF Directory.Packages.props | LTAI.DurableTask (transitive) |
| `Microsoft.DurableTask.InProcessTestHost` | **0.2.3-preview.1** | LTAI.Agent.csproj | LTAI.Agent (P8 Durable hosting) |

Note: MAF still pins DTFx 1.18.0 in its own `Directory.Packages.props`. LTAI overrides to 1.24.2 (P8 upgrade).

## Project Dependency Graph

```
LTAI.Benchmarks ─→ LTAI.Core  ─→ MAF.Agents.AI (1 PR)
                → LTAI.AI      ─→ LTAI.Core
                               ─→ MAF.OpenAI (1 PR)
                               ─→ MAF.Anthropic (1 PR)

LTAI.Core        → MAF.Agents.AI (1 PR)

LTAI.Agent       → LTAI.Core
                → LTAI.AI
                → MAF.Agents.AI + OpenAI + Tools.Shell + Workflows
                  + Workflows.Declarative + Workflows.Declarative.Mcp
                  + Harness + Hosting + Mem0 + Mcp + DurableTask
                  (11 MAF PRs total)

LTAI.TUI         → LTAI.Core + LTAI.AI + LTAI.Agent (indirect MAF)

LTAI.Desktop     → LTAI.Core + LTAI.AI + LTAI.Agent (indirect MAF)

LTAI.Web         → LTAI.Core + LTAI.AI + LTAI.Agent
                → MAF.Hosting.AspNetCore (1 PR)
                → MAF.Hosting.A2A.AspNetCore (1 PR)
                → MAF.Hosting.AGUI.AspNetCore (1 PR)
                → MAF.Hosting.OpenAI (1 PR)
                → MAF.DevUI (1 PR)
                (5 MAF PRs total)

LTAI.Cli         → LTAI.Core + LTAI.AI + LTAI.Agent (indirect MAF)
```

## MAF Extern Projects Referenced

16 of 36 MAF `dotnet/src/` projects are referenced directly by LTAI:

| MAF Project | Referenced By | LTAI Phase |
|---|---|---|
| `Microsoft.Agents.AI` | Core, Agent | Foundation |
| `Microsoft.Agents.AI.OpenAI` | AI, Agent | P1.1 |
| `Microsoft.Agents.AI.Anthropic` | AI | P1.2 |
| `Microsoft.Agents.AI.Tools.Shell` | Agent | P2 |
| `Microsoft.Agents.AI.Workflows` | Agent | P2 |
| `Microsoft.Agents.AI.Workflows.Declarative` | Agent | P7.5 |
| `Microsoft.Agents.AI.Workflows.Declarative.Mcp` | Agent | P14.7 |
| `Microsoft.Agents.AI.Harness` | Agent | P7.6 |
| `Microsoft.Agents.AI.Hosting` | Agent | P4 |
| `Microsoft.Agents.AI.Hosting.AspNetCore` | Web | P6 |
| `Microsoft.Agents.AI.Hosting.A2A.AspNetCore` | Web | P6 |
| `Microsoft.Agents.AI.Hosting.AGUI.AspNetCore` | Web | P6 |
| `Microsoft.Agents.AI.Hosting.OpenAI` | Web | P6 |
| `Microsoft.Agents.AI.Mem0` | Agent | P5.1 |
| `Microsoft.Agents.AI.Mcp` | Agent | P5.2 |
| `Microsoft.Agents.AI.DurableTask` | Agent | P8 |
| `Microsoft.Agents.AI.DevUI` | Web | P7.1 |

## Key Package Versions

| Package | Version | Managed By | Used In |
|---|---|---|---|
| `Microsoft.Extensions.AI` | 10.6.0 | LTAI | Core, AI, Agent |
| `System.ClientModel` | 1.12.0 | LTAI | AI, Agent |
| `Microsoft.ML.OnnxRuntime` | 1.21.0 | LTAI | AI |
| `Microsoft.ML.OnnxRuntime.DirectML` | 1.21.0 | LTAI | AI |
| `Microsoft.ML.OnnxRuntime.Gpu` | 1.21.0 | LTAI | AI |
| `OpenTelemetry.Extensions.Hosting` | 1.15.3 | LTAI | Core |
| `OpenTelemetry.Exporter.Console` | 1.15.3 | LTAI | Core |
| `OpenTelemetry.Exporter.OpenTelemetryProtocol` | 1.15.3 | LTAI | Core |
| `Microsoft.Data.Sqlite` | 10.0.0-preview.3 | LTAI | Agent |
| `Spectre.Console` | 0.55.2 | LTAI | TUI, Cli |
| `Avalonia` | 12.0.4 | LTAI | Desktop |
| `BenchmarkDotNet` | 0.15.0 | LTAI | Benchmarks |

## Build Times

| Project | Cold Build (--no-restore) | Notes |
|---|---|---|
| LTAI.Core | ~15s | 1 MAF PR + OTel packages |
| LTAI.AI | ~130s | 2 MAF PRs + ONNX packages |
| LTAI.Agent | ~130s | 11 MAF PRs + document processing deps |
| LTAI.Web | ~20s | 5 MAF PRs (incremental after Agent) |
| LTAI.TUI/Desktop/Cli | ~10s | Indirect MAF only |
| Solution (LTAI.sln) | ~2-3 min | Full graph evaluation |

## Model Files

3 ONNX embedding models (P14.1, Xenova INT8):

| Model | File | Size | Build Target |
|---|---|---|---|
| all-MiniLM-L6-v2 | `models/minilm-l6-v2/model.int8.onnx` | 22 MB | `DownloadEmbeddingModelMiniLM` |
| BGE-small-zh | `models/bge-small-zh/model.int8.onnx` | 23 MB | — (opt-in via download) |
| BGE-small-en | `models/bge-small-en/model.int8.onnx` | 32 MB | — (opt-in via download) |

Model directory defaults to `repo-root/models/`, overridable via `LTAI_EMBEDDING_MODELS_DIR` env var.
