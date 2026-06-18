using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using LTAI.AI;

namespace LTAI.Agent.Tools;

[ToolDomain("security")]
public static class CryptoTools
{
    [Description("计算文件的哈希值。支持 md5、sha1、sha256、sha512。\n"
        + "适用场景：文件完整性校验、重复文件检测、数据指纹验证。\n"
        + "关键参数：path — 文件路径；algorithm — 哈希算法(md5/sha1/sha256/sha512)。")]
    public static string HashFile(string path, string algorithm = "sha256")
    {
        try
        {
            if (!File.Exists(path))
                return ToolResult.Error($"File not found: {path}");

            using var stream = File.OpenRead(path);
            HashAlgorithm? hasher = algorithm.ToLowerInvariant() switch
            {
                "md5" => MD5.Create(),
                "sha1" => SHA1.Create(),
                "sha256" => SHA256.Create(),
                "sha512" => SHA512.Create(),
                _ => null
            };

            if (hasher == null)
                return ToolResult.Error($"Unsupported algorithm: {algorithm}. Supported: md5, sha1, sha256, sha512");

            using (hasher)
            {
                var hash = hasher.ComputeHash(stream);
                var hashStr = Convert.ToHexString(hash).ToLowerInvariant();
                return $"[{algorithm.ToUpperInvariant()}] {hashStr}  {path}";
            }
        }
        catch (Exception ex)
        {
            return ToolResult.Error($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    [Description("使用 AES-GCM 加密文件。\n"
        + "适用场景：加密敏感文件、保护配置文件中的密钥。\n"
        + "关键参数：path — 输入文件；password — 加密密码；outputPath — 输出加密文件路径。")]
    public static string EncryptFile(string path, string password, string outputPath)
    {
        try
        {
            if (!File.Exists(path))
                return ToolResult.Error($"File not found: {path}");

            var data = File.ReadAllBytes(path);
            var salt = RandomNumberGenerator.GetBytes(16);
            var key = DeriveKey(password, salt, 32);

            var nonce = RandomNumberGenerator.GetBytes(12);
            var ciphertext = new byte[data.Length];
            var tag = new byte[16];
            var aad = GetAssociatedData(outputPath);

                    using var aes = new AesGcm(key, 16);
            aes.Encrypt(nonce, data, ciphertext, tag, aad);

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            using var outStream = File.Create(outputPath);
            outStream.Write(salt);
            outStream.Write(nonce);
            outStream.Write(tag);
            outStream.Write(ciphertext);

            return $"Encrypted: {outputPath} ({new FileInfo(outputPath).Length} bytes)";
        }
        catch (Exception ex)
        {
            return ToolResult.Error($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    [Description("使用 AES-GCM 解密文件（与 EncryptFile 对应）。\n"
        + "适用场景：解密之前加密的文件。\n"
        + "关键参数：path — 加密文件路径；password — 解密密码；outputPath — 输出解密文件路径。")]
    public static string DecryptFile(string path, string password, string outputPath)
    {
        try
        {
            if (!File.Exists(path))
                return ToolResult.Error($"File not found: {path}");

            var data = File.ReadAllBytes(path);
            if (data.Length < 44)
                return ToolResult.Error("Invalid encrypted file format");

            var salt = data[..16];
            var nonce = data[16..28];
            var tag = data[28..44];
            var ciphertext = data[44..];

            var key = DeriveKey(password, salt, 32);
            var plaintext = new byte[ciphertext.Length];
            var aad = GetAssociatedData(outputPath);

                    using var aes = new AesGcm(key, 16);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, aad);

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            File.WriteAllBytes(outputPath, plaintext);

            return $"Decrypted: {outputPath} ({plaintext.Length} bytes)";
        }
        catch (AuthenticationTagMismatchException)
        {
            return ToolResult.Error("wrong password or corrupted file");
        }
        catch (Exception ex)
        {
            return ToolResult.Error($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    [Description("coreutils `base64` — Base64 编码/解码。decode=false 编码，decode=true 解码。\n"
        + "适用场景：将二进制编码为文本或还原。\n"
        + "参数：text — 编码或解码的文本；decode — false=编码, true=解码。")]
    public static string base64(string text, bool decode = false)
        => decode ? Base64Decode(text) : Base64Encode(text);

    [Description("coreutils `sha256sum` — 计算文件 SHA256 哈希（同 HashFile algorithm:sha256）。")]
    public static string sha256sum(string path) => HashFile(path, "sha256");

    [Description("coreutils `md5sum` — 计算文件 MD5 哈希（同 HashFile algorithm:md5）。")]
    public static string md5sum(string path) => HashFile(path, "md5");

    [Description("Base64 编码文本。\n"
        + "适用场景：将二进制数据编码为文本格式。\n"
        + "关键参数：text — 要编码的文本。")]
    public static string Base64Encode(string text)
    {
        try
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
        }
        catch (Exception ex)
        {
            return ToolResult.Error($"{ex.Message}");
        }
    }

    [Description("Base64 解码文本。\n"
        + "适用场景：将 Base64 编码的文本还原为原始文本。\n"
        + "关键参数：base64 — Base64 编码的字符串。")]
    public static string Base64Decode(string base64)
    {
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(base64));
        }
        catch (Exception ex)
        {
            return ToolResult.Error($"{ex.Message}");
        }
    }

    private static byte[] DeriveKey(string password, byte[] salt, int keySize)
    {
        return Rfc2898DeriveBytes.Pbkdf2(password, salt, 100000, HashAlgorithmName.SHA256, keySize);
    }

    private static byte[] GetAssociatedData(string filePath)
    {
        return Encoding.UTF8.GetBytes(Path.GetFileNameWithoutExtension(filePath) ?? "ltai-encrypt");
    }
}
