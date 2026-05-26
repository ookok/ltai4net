# memory: project_structure
domain: architecture
confidence: 0.95
version: 1.0.0

## summary
Key facts about the LTAI project structure, dependencies, and module organization.

## facts
- layer_model: Models → Core → Infra/Knowledge/Tools/DNA → AI → Agent → Host/Web/TUI/Desktop (confidence: 0.95)
- skill_mesh: 5-layer skill hierarchy L0(atomic) → L4(meta), all defined as .md files (confidence: 0.95)
- four_pillars: Skills(skills/) Memory(memory/) Prompts(prompts/) Tools(tools/) share same .md + Loader pattern (confidence: 0.95)
- coordinator: LTAICoordinator uses TaskQueue DAG + AgentPool for parallel multi-agent execution (confidence: 0.90)
- agent_loop: AgenticLoop implements Read→Think→Edit→Run→Observe cycle with Part streaming (confidence: 0.90)
- knowledge_graph: SQLite-based KG with PI (PredictabilityIndex) analysis, PI<0.6 triggers vector fallback (confidence: 0.85)
- config: Configuration managed via config/*.md files with OptionService, 11 sections covering 61+ keys (confidence: 0.85)

## context
Project evolved from V0.52 baseline through multiple architecture upgrades: removed CognitiveMesh, added 4-pillar .md system, unified coordinator, auto-evolution skills. Current target: net10.0.

## tags
- architecture
- project
- structure
- layers

## triggers
- pattern: "project structure" (weight: 1.0)
- pattern: "architecture" (weight: 0.9)
- pattern: "module" (weight: 0.8)
