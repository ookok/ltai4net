# Future Implementation: Remaining Stubs & Placeholders

Last updated: 2026-05-23

These are legitimate design gaps — external dependencies pending, not code quality issues.
Each has a known remediation path.

---

## Tier 1: External Library Integration (Blocked by NuGet/native dependencies)

### 1. LlamaSharpEngine — GGUF Local Inference
**File:** `src/LTAI.AI/Governors/LlamaSharpEngine.cs:80-88, 132, 149, 165`
**Current:** Returns `"[GGUF Generation - Implement with LLamaSharp 0.26.0]"` placeholder.
**Remedy:** Integrate LLamaSharp 0.26.0 NuGet, load GGUF models, call `executor.InferAsync`.

### 2. CellPackageManager — ONNX Quantization
**File:** `src/LTAI.AI/Governors/CellPackageManager.cs:361-366`
**Current:** Copies model file as-is, logs "(placeholder)".
**Remedy:** Use ONNX Runtime quantization APIs (`OrtSessionOptions.AppendExecutionProvider_Dml()` + dynamic quantization).

### 3. HardwareAcceleration — Real GPU Detection
**File:** `src/LTAI.Core/Acceleration/HardwareAcceleration.cs:44-65, 92-102`
**Current:** `DetectGPU()` returns simulated NVIDIA/AMD with hardcoded memory. `BatchEmbed()` returns empty arrays.
**Remedy:** Use `System.Management` (WMI), `DXGI`, or `CUDA` runtime for real GPU enumeration. Use ONNX Runtime DirectML execution provider for real embedding.

---

## Tier 2: External Service Integration (Needs API keys/endpoints)

### 4. ApiToolCatalog — Real API Calls
**File:** `src/LTAI.Tools/Capability/ApiCatalog/ApiToolCatalog.cs:183-184`
**Current:** Every API tool returns `$"API call: {name} with query={query}"` — no external call.
**Affected:** weather, currency, geocode, translation, SMS, image generation, etc.
**Remedy:** Register real HTTP clients per tool via `IHttpClientFactory`. Each tool needs its own API key/endpoint config.

### 5. MessageGateway — Discord Integration
**File:** `src/LTAI.Tools/Capability/Integration/MessageGateway.cs:168-174`
**Current:** `SendDiscordInternal()` returns `Status = "failed", Error = "Discord integration not yet implemented"`.
**Remedy:** Add Discord.Net NuGet, configure bot token, implement webhook/message send.

### 6. SmsGateway — Tencent SMS
**File:** `src/LTAI.Tools/Capability/Integration/SmsGateway.cs:99-102`
**Current:** `SendTencentAsync()` always returns `false`.
**Remedy:** Integrate Tencent Cloud SMS SDK (NuGet: tencentcloud-sdk-dotnet-sms).

---

## Tier 3: Agent Runtime Features (Needs design/coding)

### 7. ToolsHarnessComponent — Apply/Rollback Edits
**File:** `src/LTAI.Agent/MAF/Evolution/ToolsHarnessComponent.cs:20-24`
**Current:** `ApplyEditAsync()` and `RollbackEditAsync()` are no-ops.
**Remedy:** Implement edit application via git operations or file-system snapshots. Rollback via git revert or file restore.

### 8. LTAIAgent Session Serialization
**File:** `src/LTAI.Agent/MAF/LTAIAgent.cs:173, 182, 191`
**Current:** `CreateSessionCoreAsync` creates bare session. `Serialize/DeserializeSessionCoreAsync` return fake data.
**Remedy:** Serialize full LTAIAgentSession state (chat history, governor pipeline state, pending task handles) to JSON. Deserialize and restore.

### 9. HolisticElection — Circuit Breaker
**File:** `src/LTAI.Agent/TreeLLM/Routing/HolisticElection.cs:351-355`
**Current:** `IsCircuitClosed()` always returns `true` (circuit breaker not available).
**Remedy:** Use `Microsoft.Extensions.Http.Resilience` or Polly circuit breaker, wire via DI.

### 10. SwarmCoordinator — GetTrustedPeers
**File:** `src/LTAI.Infra/Network/Consensus/SwarmCoordinator.cs:259-262`
**Current:** Returns empty list.
**Remedy:** Implement P2P peer discovery + trust scoring via DHT or libp2p.

### 11. MoEContextProvider — Real Context Enrichment
**File:** `src/LTAI.Agent/MAF/Context/ContextProviders.cs:30-38`
**Current:** Calls `_moeQuery` but discards result, returns hardcoded "ContextMoE memory enrichment active".
**Remedy:** Actually return the MoE query result. Or remove this provider if MoE is handled elsewhere.

---

## Tier 4: Tool Governance (Planned architecture)

### 12. tool_enable / tool_disable
**File:** `src/LTAI.Tools/Capability/Tools/LTAIToolRegistry.cs:1129-1141`
**Current:** Placeholders — `tool_enable` always returns `status = "enabled"`, `tool_disable` returns `status = "disable_placeholder"`.
**Remedy:** Implement a tool gatekeeper: per-agent, per-session, per-action toggles with audit logging.

### 13. Document Redirect Tools
**File:** `src/LTAI.Tools/Capability/Tools/LTAIToolRegistry.cs:561-569`
**Current:** `doc_parse`, `text_extract`, `report_generate`, `observe_format`, `style_learn` all return hardcoded messages redirecting to other tools.
**Remedy:** Either (a) remove these and have the agent call the target tools directly, or (b) implement real document processing pipelines.

---

## Notes

- `SeedAllAsync` in `LTAI.Tools/Capability/Tools/LTAIToolRegistry.cs:16-27` is **commented out** in `Program.cs:155`. Many tools in `AllTools[]` are never registered at startup. This is intentional — tools are registered by `ToolRegistryExtensions.RegisterAllToolCategoriesAsync()` instead.
- `AddLTAIAgent()` in `LTAI.Agent/ServiceCollectionExtensions.cs:14` is never called from any entry point. All entry points call `AddLTAIMAF()`. Keep `AddLTAIAgent()` as the forward-compat path (already migrated to HandoffMeshWorkflow).
- Network `.Instance` pattern singletons (13 services) have redundant DI registrations. Consider removing DI registrations or migrating consumers to use DI resolution instead of static `.Instance`.
