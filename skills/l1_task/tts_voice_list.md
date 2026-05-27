# skill: tts_voice_list
domain: audio/tts
layer: 1
version: 1.0.0
intent: 列出可用的 Supertonic TTS 语音风格
triggers:
  - pattern: "语音风格|声音选择|voice list|有哪些声音|可选声音|voice style|choose voice"
    weight: 1.0
requires:
  - "SupertonicService registered in DI"
confidence: 0.95

## 步骤
1. 调用 SupertonicService.ListVoices() 获取所有语音
2. 返回语音列表: {Name}: {Description} (lang={Language})

## 验证
- must_contain: "M1", "F1" 至少一个
- format: markdown table with Name | Language | Description

## 默认语音
| Name | Language | Description |
|------|----------|-------------|
| M1   | en       | Deep male voice |
| M2   | en       | Warm male voice |
| M3   | en       | Bold male voice |
| M4   | en       | Calm male voice |
| M5   | en       | Friendly male voice |
| F1   | en       | Clear female voice |
| F2   | en       | Soft female voice |
| F3   | en       | Bright female voice |
| F4   | en       | Gentle female voice |
| F5   | en       | Warm female voice |
