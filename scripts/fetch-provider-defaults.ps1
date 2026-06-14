param(
    [string]$SecretsFile = "tests/secrets_export.json"
)

# Read secrets
$repoRoot = Split-Path -Parent $PSScriptRoot
$secretsPath = Join-Path $repoRoot $SecretsFile
if (-not (Test-Path $secretsPath)) { Write-Error "Secrets file not found: $secretsPath"; exit 1 }
$secrets = Get-Content $secretsPath | ConvertFrom-Json

# Known provider endpoints
$providers = @{
    "DeepSeek"    = @{ EnvVar="DEEPSEEK_API_KEY"; Endpoint="https://api.deepseek.com/v1" }
    "SiliconFlow" = @{ EnvVar="SILICONFLOW_API_KEY"; Endpoint="https://api.siliconflow.cn/v1" }
    "Aliyun(Qwen)" = @{ EnvVar="DASHSCOPE_API_KEY"; Endpoint="https://dashscope.aliyuncs.com/compatible-mode/v1" }
    "Zhipu(GLM)"  = @{ EnvVar="ZHIPU_API_KEY"; Endpoint="https://open.bigmodel.cn/api/paas/v4" }
    "Hunyuan"     = @{ EnvVar="HUNYUAN_API_KEY"; Endpoint="https://api.hunyuan.cloud.tencent.com/v1" }
    "Moonshot(Kimi)" = @{ EnvVar="MOONSHOT_API_KEY"; Endpoint="https://api.moonshot.cn/v1" }
    "Baichuan"    = @{ EnvVar="BAICHUAN_API_KEY"; Endpoint="https://api.baichuan-ai.com/v1" }
    "Yi(01.AI)"   = @{ EnvVar="YI_API_KEY"; Endpoint="https://api.lingyiwanwu.com/v1" }
    "StepFun"     = @{ EnvVar="STEP_API_KEY"; Endpoint="https://api.stepfun.com/v1" }
    "Minimax"     = @{ EnvVar="MINIMAX_API_KEY"; Endpoint="https://api.minimax.chat/v1" }
    "OpenAI"      = @{ EnvVar="OPENAI_API_KEY"; Endpoint="https://api.openai.com/v1" }
    "Groq"        = @{ EnvVar="GROQ_API_KEY"; Endpoint="https://api.groq.com/openai/v1" }
    "OpenRouter"  = @{ EnvVar="OPENROUTER_API_KEY"; Endpoint="https://openrouter.ai/api/v1" }
    "Together AI" = @{ EnvVar="TOGETHER_API_KEY"; Endpoint="https://api.together.xyz/v1" }
    "Mistral"     = @{ EnvVar="MISTRAL_API_KEY"; Endpoint="https://api.mistral.ai/v1" }
    "Perplexity"  = @{ EnvVar="PERPLEXITY_API_KEY"; Endpoint="https://api.perplexity.ai" }
    "X.AI(Grok)"  = @{ EnvVar="XAI_API_KEY"; Endpoint="https://api.x.ai/v1" }
    "Cohere"      = @{ EnvVar="COHERE_API_KEY"; Endpoint="https://api.cohere.ai/v1" }
    "Fireworks AI"= @{ EnvVar="FIREWORKS_API_KEY"; Endpoint="https://api.fireworks.ai/inference/v1" }
    "小米 MiMo"   = @{ EnvVar="MIMO_API_KEY"; Endpoint="https://api.xiaomimimo.com/v1" }
    "Doubao"      = @{ EnvVar="DOUBAO_API_KEY"; Endpoint="https://ark.cn-beijing.volces.com/api/v3" }
}

$results = [ordered]@{}
$secretsMap = @{}
$secrets.PSObject.Properties | ForEach-Object { $secretsMap[$_.Name.ToLower()] = "$($_.Value)" }

# Custom mapping: secrets file key (lowercase) -> env var name
$secretToEnv = @{
    "aliyun_api_key"     = "DASHSCOPE_API_KEY"
    "deepseek_api_key"   = "DEEPSEEK_API_KEY"
    "siliconflow_api_key" = "SILICONFLOW_API_KEY"
    "zhipu_api_key"      = "ZHIPU_API_KEY"
    "hunyuan_api_key"    = "HUNYUAN_API_KEY"
    "bailing_api_key"    = "BAICHUAN_API_KEY"
    "xiaomi_api_key"     = "MIMO_API_KEY"
    "stepfun_api_key"    = "STEP_API_KEY"
    "openrouter_api_key" = "OPENROUTER_API_KEY"
    "spark_api_key"      = "SPARK_API_KEY"
    "baidu_api_key"      = "BAIDU_API_KEY"
    "dmxapi_api_key"     = "DMXAPI_API_KEY"
    "nvidia_api_key"     = "NVIDIA_API_KEY"
    "internlm_api_key"   = "INTERNLM_API_KEY"
    "modelscope_api_key" = "MODELSCOPE_API_KEY"
    "sensetime_api_key"  = "SENSETIME_API_KEY"
    "longcat_api_key"    = "LONGCAT_API_KEY"
    "mofang_api_key"     = "MOFANG_API_KEY"
}

Write-Host "`nFetching first model from each provider..." -ForegroundColor Cyan
Write-Host "=========================================" -ForegroundColor Cyan

foreach ($entry in $providers.GetEnumerator() | Sort-Object Name)
{
    $name = $entry.Key
    $info = $entry.Value
    $envLower = $info.EnvVar.ToLower()
    # Convert canonical env var name to secrets file key via custom mapping
    $secretsKey = ($secretToEnv.GetEnumerator() | Where-Object { $_.Value -eq $info.EnvVar } | Select-Object -First 1).Key
    if (-not $secretsKey) { $secretsKey = $envLower }
    $apiKey = if ($secretsMap.ContainsKey($secretsKey)) { $secretsMap[$secretsKey] } else { $null }

    if ([string]::IsNullOrEmpty($apiKey))
    {
        Write-Host "  $name -> [MISSING KEY]" -ForegroundColor DarkYellow
        $results[$name] = $null
        continue
    }

    $url = "$($info.Endpoint.TrimEnd('/'))/models"
    try
    {
        $resp = Invoke-WebRequest -Uri $url -Headers @{ Authorization = "Bearer $apiKey" } -TimeoutSec 10 -ErrorAction Stop
        $json = $resp.Content | ConvertFrom-Json
        $firstModel = $json.data[0].id
        Write-Host "  $name -> $firstModel" -ForegroundColor Green
        $results[$name] = $firstModel
    }
    catch
    {
        $errMsg = $_.Exception.Message
        if ($errMsg.Length -gt 80) { $errMsg = $errMsg.Substring(0, 80) + "..." }
        Write-Host "  $name -> [ERROR] $errMsg" -ForegroundColor Red
        $results[$name] = $null
    }
}

Write-Host "`n`nResults (copy-paste into KnownKeys):" -ForegroundColor Cyan
Write-Host "=====================================" -ForegroundColor Cyan
foreach ($entry in $results.GetEnumerator() | Sort-Object Name)
{
    if ($entry.Value)
    {
        Write-Host "  $($entry.Key): $($entry.Value)"
    }
}
# Clear sensitive data from memory
$secretsMap.Clear()
Remove-Variable secretsMap -ErrorAction SilentlyContinue
Remove-Variable secrets -ErrorAction SilentlyContinue
