# option: provider_endpoints
section: LTAI:AI:Providers
description: AI provider endpoint URLs and default model names. API keys read from env vars (e.g. DEEPSEEK_API_KEY), never stored here. Edit this file to change endpoints or models.

## keys
- deepseek.endpoint: string (default: https://api.deepseek.com) — DeepSeek API
  env: DEEPSEEK_ENDPOINT
- deepseek.model: string (default: deepseek-v4-pro) — DeepSeek deep model
  env: DEEPSEEK_MODEL
- deepseek-fast.endpoint: string (default: https://api.deepseek.com) — DeepSeek fast tier
  env: DEEPSEEK_FAST_ENDPOINT
- deepseek-fast.model: string (default: deepseek-v4-flash) — DeepSeek flash model
  env: DEEPSEEK_FAST_MODEL
- openai.endpoint: string (default: https://api.openai.com/v1) — OpenAI API
  env: OPENAI_ENDPOINT
- openai.model: string (default: gpt-4o) — OpenAI default model
  env: OPENAI_MODEL
- anthropic.endpoint: string (default: https://api.anthropic.com/v1) — Anthropic API
  env: ANTHROPIC_ENDPOINT
- anthropic.model: string (default: claude-sonnet-4-20250514) — Anthropic default model
  env: ANTHROPIC_MODEL
- dashscope.endpoint: string (default: https://dashscope.aliyuncs.com/compatible-mode/v1) — Alibaba DashScope (Qwen)
  env: DASHSCOPE_ENDPOINT
- dashscope.model: string (default: qwen-max) — Qwen deep model
  env: DASHSCOPE_MODEL
- dashscope-fast.endpoint: string (default: https://dashscope.aliyuncs.com/compatible-mode/v1) — Qwen fast tier
  env: DASHSCOPE_FAST_ENDPOINT
- dashscope-fast.model: string (default: qwen-turbo) — Qwen fast model
  env: DASHSCOPE_FAST_MODEL
- siliconflow.endpoint: string (default: https://api.siliconflow.cn/v1) — SiliconFlow
  env: SILICONFLOW_ENDPOINT
- siliconflow.model: string (default: deepseek-ai/DeepSeek-V3) — SiliconFlow default model
  env: SILICONFLOW_MODEL
- zhipu.endpoint: string (default: https://open.bigmodel.cn/api/paas/v4) — Zhipu AI (GLM)
  env: ZHIPU_ENDPOINT
- zhipu.model: string (default: glm-4-plus) — Zhipu default model
  env: ZHIPU_MODEL
- hunyuan.endpoint: string (default: https://api.hunyuan.cloud.tencent.com/v1) — Tencent Hunyuan
  env: HUNYUAN_ENDPOINT
- hunyuan.model: string (default: hunyuan-turbos-latest) — Hunyuan default model
  env: HUNYUAN_MODEL
- baidu.endpoint: string (default: https://qianfan.baidubce.com/v2) — Baidu Qianfan
  env: BAIDU_ENDPOINT
- baidu.model: string (default: ernie-4.0-turbo-128k) — Baidu default model
  env: BAIDU_MODEL
- spark.endpoint: string (default: https://spark-api-open.xf-yun.com/v1) — iFlytek Spark
  env: SPARK_ENDPOINT
- spark.model: string (default: spark-4.0-ultra) — Spark default model
  env: SPARK_MODEL
- volcengine.endpoint: string (default: https://ark.cn-beijing.volces.com/api/v3) — Volcengine (Doubao)
  env: VOLCENGINE_ENDPOINT
- volcengine.model: string (default: doubao-pro-256k) — Doubao default model
  env: VOLCENGINE_MODEL
- moonshot.endpoint: string (default: https://api.moonshot.cn/v1) — Moonshot (Kimi)
  env: MOONSHOT_ENDPOINT
- moonshot.model: string (default: moonshot-v1-128k) — Kimi default model
  env: MOONSHOT_MODEL
- minimax.endpoint: string (default: https://api.minimax.chat/v1) — MiniMax
  env: MINIMAX_ENDPOINT
- minimax.model: string (default: abab7-chat) — MiniMax default model
  env: MINIMAX_MODEL
- groq.endpoint: string (default: https://api.groq.com/openai/v1) — Groq
  env: GROQ_ENDPOINT
- groq.model: string (default: llama-4-maverick-128k-instruct) — Groq default model
  env: GROQ_MODEL
- openrouter.endpoint: string (default: https://openrouter.ai/api/v1) — OpenRouter
  env: OPENROUTER_ENDPOINT
- openrouter.model: string (default: openai/gpt-4o) — OpenRouter default model
  env: OPENROUTER_MODEL
- gemini.endpoint: string (default: https://generativelanguage.googleapis.com/v1beta) — Google Gemini
  env: GEMINI_ENDPOINT
- gemini.model: string (default: gemini-2.5-pro) — Gemini default model
  env: GEMINI_MODEL
- ollama.endpoint: string (default: http://localhost:11434/v1) — Ollama local
  env: OLLAMA_ENDPOINT
- ollama.model: string (default: qwen3) — Ollama default model
  env: OLLAMA_MODEL
- mofang.endpoint: string (default: https://api.mofang.ai/v1) — MoFang
  env: MOFANG_ENDPOINT
- mofang.model: string (default: deepseek-v3) — MoFang default model
  env: MOFANG_MODEL
- nvidia.endpoint: string (default: https://integrate.api.nvidia.com/v1) — NVIDIA NIM
  env: NVIDIA_ENDPOINT
- nvidia.model: string (default: meta/llama-4-maverick-17b-128e-instruct) — NVIDIA default model
  env: NVIDIA_MODEL
- modelscope.endpoint: string (default: https://api-inference.modelscope.cn/v1) — ModelScope
  env: MODELSCOPE_ENDPOINT
- modelscope.model: string (default: qwen-max) — ModelScope default model
  env: MODELSCOPE_MODEL
- azure.endpoint: string (default: ) — Azure OpenAI
  env: AZURE_AI_ENDPOINT
- azure.model: string (default: gpt-4o) — Azure default model
  env: AZURE_AI_MODEL
- kiro.endpoint: string (default: https://api.kiro.cn/v1) — Kiro
  env: KIRO_ENDPOINT
- kiro.model: string (default: kiro-latest) — Kiro default model
  env: KIRO_MODEL
- xiaomi.endpoint: string (default: https://api.xiaomi-ai.com/v1) — Xiaomi
  env: XIAOMI_ENDPOINT
- xiaomi.model: string (default: mi-ai-large) — Xiaomi default model
  env: XIAOMI_MODEL
- stepfun.endpoint: string (default: https://api.stepfun.com/v1) — StepFun (阶跃星辰)
  env: STEPFUN_ENDPOINT
- stepfun.model: string (default: step-2-16k) — StepFun default model
  env: STEPFUN_MODEL
- internlm.endpoint: string (default: https://api.internlm.com/v1) — InternLM (书生)
  env: INTERNLM_ENDPOINT
- internlm.model: string (default: internlm3-8b) — InternLM default model
  env: INTERNLM_MODEL
- sensetime.endpoint: string (default: https://api.sensetime.com/v1) — SenseTime (商汤)
  env: SENSETIME_ENDPOINT
- sensetime.model: string (default: sensechat-5) — SenseTime default model
  env: SENSETIME_MODEL
- longcat.endpoint: string (default: https://api.longcat.ai/v1) — LongCat
  env: LONGCAT_ENDPOINT
- longcat.model: string (default: longcat-flash) — LongCat default model
  env: LONGCAT_MODEL
- dmxapi.endpoint: string (default: https://api.dmxapi.com/v1) — DMXAPI
  env: DMXAPI_ENDPOINT
- dmxapi.model: string (default: gpt-4o) — DMXAPI default model
  env: DMXAPI_MODEL
- xai.endpoint: string (default: https://api.x.ai/v1) — xAI (Grok)
  env: XAI_ENDPOINT
- xai.model: string (default: grok-3) — xAI default model
  env: XAI_MODEL
- opencode.endpoint: string (default: https://api.opencode.ai/v1) — OpenCode
  env: OPENCODE_ENDPOINT
- opencode.model: string (default: deepseek-v4-pro) — OpenCode default model
  env: OPENCODE_MODEL
- kunlun.endpoint: string (default: https://api.skylark.cn/v1) — Kunlun Skylark (昆仑万维/天工)
  env: KUNLUN_ENDPOINT
- kunlun.model: string (default: skylark-4) — Skylark default model
  env: KUNLUN_MODEL

## tags
- provider
- endpoint
- model
- configuration
