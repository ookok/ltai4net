using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Tools.Integration;

public sealed class WXBizMsgCrypt
{
    public static readonly Lazy<WXBizMsgCrypt> Instance = new(() => new WXBizMsgCrypt());

    private readonly ILogger<WXBizMsgCrypt> _logger;
    private string _token = "";
    private byte[] _aesKey = Array.Empty<byte>();
    private string _corpId = "";

    private WXBizMsgCrypt(ILogger<WXBizMsgCrypt>? logger = null)
    {
        _logger = logger ?? NullLogger<WXBizMsgCrypt>.Instance;
    }

    public void Configure(string token, string encodingAesKey, string corpId)
    {
        _token = token;
        _corpId = corpId;

        if (encodingAesKey.Length == 43)
            encodingAesKey += "=";

        _aesKey = Convert.FromBase64String(encodingAesKey);
    }

    public bool VerifySignature(string signature, string timestamp, string nonce, string echostr)
    {
        if (string.IsNullOrEmpty(_token)) return false;
        var expected = SHA1Sort(_token, timestamp, nonce, echostr);
        return string.Equals(expected, signature, StringComparison.OrdinalIgnoreCase);
    }

    public string DecryptMsg(string signature, string timestamp, string nonce, string encryptedXml)
    {
        if (string.IsNullOrEmpty(_token) || _aesKey.Length == 0)
            return "";

        if (!VerifySignature(signature, timestamp, nonce, encryptedXml))
        {
            _logger.LogWarning("WeWork signature verification failed");
            return "";
        }

        try
        {
            var base64 = ExtractEncryptContent(encryptedXml);
            var plain = AesDecrypt(base64);
            return ExtractMessage(plain);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WeWork message decryption failed");
            return "";
        }
    }

    public string EncryptMsg(string replyXml, string timestamp, string nonce)
    {
        if (_aesKey.Length == 0 || string.IsNullOrEmpty(_corpId))
            return "";

        try
        {
            var random = new byte[16];
            RandomNumberGenerator.Fill(random);

            var msgBytes = Encoding.UTF8.GetBytes(replyXml);
            var corpBytes = Encoding.UTF8.GetBytes(_corpId);
            var lenBytes = BitConverter.GetBytes(IPAddress.HostToNetworkOrder(msgBytes.Length));

            var plain = new byte[16 + 4 + msgBytes.Length + corpBytes.Length];
            Buffer.BlockCopy(random, 0, plain, 0, 16);
            Buffer.BlockCopy(lenBytes, 0, plain, 16, 4);
            Buffer.BlockCopy(msgBytes, 0, plain, 20, msgBytes.Length);
            Buffer.BlockCopy(corpBytes, 0, plain, 20 + msgBytes.Length, corpBytes.Length);

            var encrypted = AesEncrypt(plain);
            var signature = SHA1Sort(_token, timestamp, nonce, encrypted);

            return JsonSerializer.Serialize(new
            {
                encrypt = encrypted,
                msgsignature = signature,
                timestamp,
                nonce
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WeWork message encryption failed");
            return "";
        }
    }

    public static string SHA1Sort(params string[] items)
    {
        var sorted = items.OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var concatenated = string.Concat(sorted);
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(concatenated));
        return Convert.ToHexStringLower(hash);
    }

    private byte[] AesDecrypt(string base64Cipher)
    {
        var cipher = Convert.FromBase64String(base64Cipher);
        var iv = _aesKey[..16];

        using var aes = Aes.Create();
        aes.Key = _aesKey;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;

        using var decryptor = aes.CreateDecryptor();
        var decrypted = decryptor.TransformFinalBlock(cipher, 0, cipher.Length);
        return Pkcs7Unpad(decrypted);
    }

    private string AesEncrypt(byte[] plain)
    {
        var iv = _aesKey[..16];
        var padded = Pkcs7Pad(plain, 32);

        using var aes = Aes.Create();
        aes.Key = _aesKey;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;

        using var encryptor = aes.CreateEncryptor();
        var encrypted = encryptor.TransformFinalBlock(padded, 0, padded.Length);
        return Convert.ToBase64String(encrypted);
    }

    private static byte[] Pkcs7Pad(byte[] data, int blockSize)
    {
        var padLen = blockSize - data.Length % blockSize;
        var padded = new byte[data.Length + padLen];
        Buffer.BlockCopy(data, 0, padded, 0, data.Length);
        for (var i = data.Length; i < padded.Length; i++)
            padded[i] = (byte)padLen;
        return padded;
    }

    private static byte[] Pkcs7Unpad(byte[] data)
    {
        if (data.Length == 0) return data;
        var padLen = data[^1];
        if (padLen < 1 || padLen > 32)
            return data;
        return data[..^padLen];
    }

    private static string ExtractEncryptContent(string xmlBody)
    {
        try
        {
            var doc = XDocument.Parse(xmlBody);
            var encrypt = doc.Root?.Element("Encrypt");
            return encrypt?.Value ?? xmlBody;
        }
        catch
        {
            return xmlBody;
        }
    }

    private string ExtractMessage(byte[] plain)
    {
        var len = IPAddress.NetworkToHostOrder(BitConverter.ToInt32(plain, 16));
        var msgBytes = new byte[len];
        Buffer.BlockCopy(plain, 20, msgBytes, 0, len);

        var corpLen = plain.Length - 20 - len;
        var corpBytes = new byte[corpLen];
        Buffer.BlockCopy(plain, 20 + len, corpBytes, 0, corpLen);
        var corpId = Encoding.UTF8.GetString(corpBytes);

        if (corpId != _corpId)
        {
            _logger.LogWarning("WeWork corpId mismatch: expected {Expected}, got {Actual}", _corpId, corpId);
        }

        return Encoding.UTF8.GetString(msgBytes);
    }
}
