# skill: tts_speak
domain: audio/tts
layer: 1
version: 1.0.0
intent: 使用 Supertonic ONNX TTS 朗读文本，生成语音输出
triggers:
  - pattern: "朗读|读出|speak|read aloud|播音|念|读出来|语音合成|tts|text.to.speech"
    weight: 1.0
  - pattern: "把.*读出来|帮.*念|帮我.*说|说出.*内容"
    weight: 0.9
requires:
  - "Supertonic ONNX model (assets/supertonic/)"
  - "ONNX Runtime"
confidence: 0.85

## 参数
- `text`: 要朗读的文本内容 (必填)
- `language`: 语言代码，默认 "en"，支持31种语言
- `voice`: 语音风格，可选 M1-M5, F1-F5，默认 "M1"
- `speed`: 语速 0.3-3.0，默认 1.0
- `expression`: 表情标签，如 <laugh>, <breath>, <sigh>

## 步骤
1. 验证 text 不为空，长度不超过 5000 字符
2. 验证 language 在 SupertonicLanguages.Supported 中
3. 调用 SupertonicService.SynthesizeAsync() 
4. 检查 result.Success，如失败返回 error
5. 将 result.WavBytes 保存为 .wav 文件或流式播放

## 验证
- must_contain: "Success = true"
- output: .wav file at 44100Hz 16-bit mono
