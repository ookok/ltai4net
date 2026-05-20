using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LTAI.Core.Configuration;

public sealed class SecretVault
{
    private static readonly Lazy<SecretVault> _instance = new(() => new SecretVault());
    public static SecretVault Instance => _instance.Value;

    private readonly string _secretFile;
    private readonly byte[] _machineKey;
    private readonly Dictionary<string, string> _cache = new();
    private bool _loaded;
    private readonly object _lock = new();

    private SecretVault(string? secretFile = null)
    {
        _secretFile = secretFile ?? global::System.IO.Path.Combine("config", "secrets.enc");
        _machineKey = DeriveMachineKey();
    }

    public string Get(string key, string defaultValue = "")
    {
        EnsureLoaded();
        lock (_lock) { return _cache.GetValueOrDefault(key, defaultValue); }
    }

    public bool TryGet(string key, out string value)
    {
        EnsureLoaded();
        lock (_lock) { return _cache.TryGetValue(key, out value!); }
    }

    public void Set(string key, string value)
    {
        EnsureLoaded();
        lock (_lock) { _cache[key] = value; }
        Save();
    }

    public bool Delete(string key)
    {
        EnsureLoaded();
        bool removed;
        lock (_lock) { removed = _cache.Remove(key); }
        if (removed) Save();
        return removed;
    }

    public List<string> Keys()
    {
        EnsureLoaded();
        lock (_lock) { return _cache.Keys.ToList(); }
    }

    public Dictionary<string, string> GetAll()
    {
        EnsureLoaded();
        lock (_lock) { return new Dictionary<string, string>(_cache); }
    }

    public int SeedDefaults()
    {
        EnsureLoaded();
        var count = 0;

        var envMap = new Dictionary<string, string>
        {
            ["DEEPSEEK_API_KEY"] = "deepseek_api_key",
            ["OPENAI_API_KEY"] = "openai_api_key",
            ["ANTHROPIC_API_KEY"] = "anthropic_api_key",
            ["SILICONFLOW_API_KEY"] = "siliconflow_api_key",
            ["MOFANG_API_KEY"] = "mofang_api_key",
            ["NVIDIA_API_KEY"] = "nvidia_api_key",
            ["ALIYUN_API_KEY"] = "aliyun_api_key",
            ["ZHIPU_API_KEY"] = "zhipu_api_key",
            ["HUNYUAN_API_KEY"] = "hunyuan_api_key",
            ["BAIDU_API_KEY"] = "baidu_api_key",
            ["SPARK_API_KEY"] = "spark_api_key",
            ["BAILING_API_KEY"] = "bailing_api_key",
            ["STEPFUN_API_KEY"] = "stepfun_api_key",
            ["INTERNLM_API_KEY"] = "internlm_api_key",
            ["SENSETIME_API_KEY"] = "sensetime_api_key",
            ["MODELSCOPE_API_KEY"] = "modelscope_api_key",
            ["OPENROUTER_API_KEY"] = "openrouter_api_key",
            ["XIAOMI_API_KEY"] = "xiaomi_api_key",
            ["LONGCAT_API_KEY"] = "longcat_api_key",
            ["DMXAPI_API_KEY"] = "dmxapi_api_key",
            ["VOLLCENGINE_API_KEY"] = "volcengine_api_key",
            ["MOONSHOT_API_KEY"] = "moonshot_api_key",
            ["GEMINI_API_KEY"] = "gemini_api_key",
            ["MINIMAX_API_KEY"] = "minimax_api_key",
            ["GROQ_API_KEY"] = "groq_api_key",
            ["KIRO_API_KEY"] = "kiro_api_key",
            ["OPENCODE_API_KEY"] = "opencode_api_key",
            ["LT_BUILTIN_SENSETIME_KEY"] = "sensetime_api_key",
            ["LT_BUILTIN_TIANDITU_KEY"] = "tianditu_key",
            ["BAIDU_MAP_AK"] = "baidu_map_ak",
            ["BAIDU_MAP_SK"] = "baidu_map_sk",
            ["TENCENT_MAP_KEY"] = "tencent_map_key",
            ["AMAP_KEY"] = "amap_key",
            ["OPENWEATHERMAP_API_KEY"] = "openweathermap_api_key",
            ["QWEATHER_API_KEY"] = "qweather_api_key",
            ["BAIDU_TRANSLATE_APPID"] = "baidu_translate_appid",
            ["BAIDU_TRANSLATE_KEY"] = "baidu_translate_key",
            ["UNSPLASH_ACCESS_KEY"] = "unsplash_access_key",
            ["UNSPLASH_SECRET_KEY"] = "unsplash_secret_key",
            ["PIXABAY_API_KEY"] = "pixabay_api_key",
            ["GITHUB_TOKEN"] = "github_token",
            ["SMTP_USER"] = "smtp_user",
            ["SMTP_PASS"] = "smtp_pass",
            ["SMTP_HOST"] = "smtp_host",
            ["SMTP_PORT"] = "smtp_port",
            ["OLLAMA_HOST"] = "ollama_host"
        };

        lock (_lock)
        {
            if (_cache.Count > 0) return 0;

            foreach (var (envVar, secretKey) in envMap)
            {
                var value = Environment.GetEnvironmentVariable(envVar);
                if (!string.IsNullOrEmpty(value) && !_cache.ContainsKey(secretKey))
                {
                    _cache[secretKey] = value;
                    count++;
                }
            }
        }

        if (count > 0) Save();
        return count;
    }

    public Dictionary<string, string> ExportEnv()
    {
        EnsureLoaded();
        lock (_lock)
        {
            var result = new Dictionary<string, string>();
            foreach (var (k, v) in _cache)
                result[$"LT_SECRET_{k}"] = v;
            return result;
        }
    }

    private void EnsureLoaded()
    {
        if (_loaded) return;

        lock (_lock)
        {
            if (_loaded) return;

            if (global::System.IO.File.Exists(_secretFile))
            {
                try
                {
                    var data = global::System.IO.File.ReadAllBytes(_secretFile);
                    var plaintext = Decrypt(data);
                    var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(plaintext);
                    if (loaded != null)
                    {
                        foreach (var kvp in loaded)
                            _cache[kvp.Key] = kvp.Value;
                    }
                    Save();
                }
                catch { /* non-fatal */ }
            }

            _loaded = true;
        }
    }

    private void Save()
    {
        var dir = global::System.IO.Path.GetDirectoryName(_secretFile);
        if (dir != null) global::System.IO.Directory.CreateDirectory(dir);

        var plaintext = JsonSerializer.Serialize(_cache);
        var encrypted = Encrypt(plaintext);
        global::System.IO.File.WriteAllBytes(_secretFile, encrypted);
    }

    private byte[] Encrypt(string plaintext)
    {
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var nonce = new byte[12];
        RandomNumberGenerator.Fill(nonce);

        var ciphertext = new byte[plainBytes.Length];
        var tag = new byte[16];

        using var aes = new AesGcm(_machineKey[..32], 16);
        aes.Encrypt(nonce, plainBytes, ciphertext, tag);

        var result = new byte[nonce.Length + tag.Length + ciphertext.Length];
        Buffer.BlockCopy(nonce, 0, result, 0, nonce.Length);
        Buffer.BlockCopy(tag, 0, result, nonce.Length, tag.Length);
        Buffer.BlockCopy(ciphertext, 0, result, nonce.Length + tag.Length, ciphertext.Length);

        return result;
    }

    private string Decrypt(byte[] data)
    {
        if (data.Length < 28) throw new ArgumentException("Ciphertext too short");

        if (data.Length >= 4 && data[0] == (byte)'g' && data[1] == (byte)'A')
            return DecryptFernet(data);

        var nonce = new byte[12];
        var tag = new byte[16];
        var ciphertext = new byte[data.Length - 28];

        Buffer.BlockCopy(data, 0, nonce, 0, 12);
        Buffer.BlockCopy(data, 12, tag, 0, 16);
        Buffer.BlockCopy(data, 28, ciphertext, 0, ciphertext.Length);

        var plainBytes = new byte[ciphertext.Length];
        using var aes = new AesGcm(_machineKey[..32], 16);
        aes.Decrypt(nonce, ciphertext, tag, plainBytes);

        return Encoding.UTF8.GetString(plainBytes);
    }

    private string DecryptFernet(byte[] data)
    {
        var b64 = Encoding.UTF8.GetString(data);
        var raw = Convert.FromBase64String(b64.Replace('-', '+').Replace('_', '/'));

        if (raw.Length < 57) throw new ArgumentException("Invalid Fernet token");

        var iv = new byte[16];
        Buffer.BlockCopy(raw, 9, iv, 0, 16);
        var ciphertext = new byte[raw.Length - 57];
        Buffer.BlockCopy(raw, 25, ciphertext, 0, ciphertext.Length);
        var hmac = new byte[32];
        Buffer.BlockCopy(raw, raw.Length - 32, hmac, 0, 32);

        var signingInput = new byte[raw.Length - 32];
        Buffer.BlockCopy(raw, 0, signingInput, 0, signingInput.Length);

        var fernetKey = _machineKey[..16];
        var signingKey = _machineKey[16..32];

        using var hmacSha256 = new HMACSHA256(signingKey);
        var computedHmac = hmacSha256.ComputeHash(signingInput);
        if (!computedHmac.SequenceEqual(hmac))
            throw new InvalidOperationException("Fernet HMAC verification failed");

        using var aes = Aes.Create();
        aes.Key = fernetKey;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        using var decryptor = aes.CreateDecryptor();
        var plainBytes = decryptor.TransformFinalBlock(ciphertext, 0, ciphertext.Length);
        return Encoding.UTF8.GetString(plainBytes);
    }

    private static byte[] DeriveMachineKey()
    {
        var interfaces = global::System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces();
        var macAddress = interfaces
            .Where(n => n.OperationalStatus == global::System.Net.NetworkInformation.OperationalStatus.Up)
            .Select(n => n.GetPhysicalAddress().ToString())
            .FirstOrDefault() ?? "000000000000";
        long macLong;
        try { macLong = Convert.ToInt64(macAddress, 16); } catch { macLong = 0; }

        var parts = new[]
        {
            Environment.MachineName,
            macLong.ToString(),
            "livingtree-secret-vault-v2"
        };
        var combined = string.Join("|", parts);
        return SHA256.HashData(Encoding.UTF8.GetBytes(combined));
    }
}
