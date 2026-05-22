using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace LTAI.Capability.CodeGraph;

/// <summary>
/// SimHash 实现，用于生成代码的二进制指纹
/// 广泛应用于代码去重、克隆检测和相似性搜索
/// </summary>
public static class SimHash
{
    /// <summary>
    /// 计算文本的 64 位 SimHash
    /// </summary>
    public static ulong Compute(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;

        var v = new long[64];
        var tokens = Tokenize(text);

        foreach (var token in tokens)
        {
            var hash = GetHash(token);
            for (var i = 0; i < 64; i++)
            {
                var bitmask = 1UL << i;
                if ((hash & bitmask) != 0)
                    v[i] += 1;
                else
                    v[i] -= 1;
            }
        }

        ulong fingerprint = 0;
        for (var i = 0; i < 64; i++)
        {
            if (v[i] > 0)
                fingerprint |= 1UL << i;
        }

        return fingerprint;
    }

    /// <summary>
    /// 计算两个指纹的汉明距离
    /// </summary>
    public static int Distance(ulong hash1, ulong hash2)
    {
        var xor = hash1 ^ hash2;
        return BitOperations.PopCount(xor);
    }

    /// <summary>
    /// 计算相似度 (0.0 - 1.0)
    /// 通常汉明距离 <= 3 视为相似
    /// </summary>
    public static double Similarity(ulong hash1, ulong hash2)
    {
        var dist = Distance(hash1, hash2);
        return 1.0 - (dist / 64.0);
    }

    private static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        var sb = new StringBuilder();
        
        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                if (sb.Length > 0)
                {
                    tokens.Add(sb.ToString());
                    sb.Clear();
                }
            }
        }
        if (sb.Length > 0) tokens.Add(sb.ToString());
        
        return tokens;
    }

    private static ulong GetHash(string token)
    {
        using var md5 = MD5.Create();
        var bytes = md5.ComputeHash(Encoding.UTF8.GetBytes(token));
        return BitConverter.ToUInt64(bytes, 0);
    }
}
