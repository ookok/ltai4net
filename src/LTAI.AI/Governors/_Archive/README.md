# _Archive — Dead Code Archive

These files were moved here during the 2026-05-29 comprehensive audit.

**Reason:** They exist in the source tree but have no DI registration, no known
consumers, and thus are never reached at runtime. They compile but never execute.

**Plan:** Keep here for 1–2 release cycles. If no one revives them, delete
permanently.

**To revive a file:**
1. Add DI registration (typically `services.AddSingleton<T>()` /
   `AddScoped<T>()`)
2. Wire up consumers via constructor injection
3. Move file back to `src/LTAI.AI/Governors/`
4. Remove from the `Compile Remove` glob in `.csproj`

---

## Files archived (17 total)

| File | What it is | Notes |
|------|-----------|-------|
| `AbTestingFramework.cs` | A/B test framework | No DI, no consumers |
| `CognitionSeeder.cs` | Initial cognition seeding | No DI, no consumers |
| `DataflowPipeline.cs` | Dataflow processing pipeline | No DI, no consumers |
| `DomainGraphRegistry.cs` | Domain graph registry | No DI, no consumers |
| `GrpoPromptOptimizer.cs` | GRPO-based prompt tuning | No DI, no consumers |
| `HeuristicLearning.cs` | Heuristic learning module | No DI, no consumers |
| `IslandSampler.cs` | Island-based sampling | No DI, no consumers |
| `KnowledgeGapDetector.cs` | Knowledge gap detection | No DI, no consumers |
| `MultiPolicyTrainer.cs` | Multi-policy reinforcement | No DI, no consumers |
| `ParallelExperimentRunner.cs` | Parallel experiment executor | No DI, no consumers |
| `PathCompressor.cs` | Path compression utilities | No DI, no consumers |
| `QValueEstimator.cs` | Q-value estimation | No DI, no consumers |
| `SePTDataCollector.cs` | SePT data collection | No DI, no consumers |
| `SharedReplayBuffer.cs` | Shared replay buffer | No DI, no consumers |
| `SkillEvolutionBridge.cs` | Skill evolution bridge | No DI, no consumers |
| `SupertonicModels.cs` | ONNX TTS model data | Companion to SupertonicService |
| `SupertonicService.cs` | ONNX TTS engine | No DI, no consumers |

## Files moved back (9 — compile-time dependencies found)

| File | Consumer(s) |
|------|-------------|
| `LatentState.cs` | IL1InferenceEngine, LlamaSharpEngine, RecursiveLink, RecursiveLatentPipeline |
| `SemanticQueryCache.cs` | AtlasFunctionalToken |
| `WeightSubspaceAnalyzer.cs` | FederatedLearningService |
| `NeuralDependencyGraph.cs` | StructureAwareRouter |
| `CellAnswerStore.cs` | TeachingRuleExtractor |
| `ToolSelector.cs` | ReActLoopOrchestrator |
| `DreamCycle.cs` | ResponsePostProcessor |
| `ExperimentAnalyzer.cs` | SkillGraphEvolver (cross-project) |
| `VerifiableRegistry.cs` | CLI DebugObservability (runtime reflection) |
