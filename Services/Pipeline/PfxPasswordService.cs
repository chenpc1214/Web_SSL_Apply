using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SslWebConsole.Services;

/// <summary>
/// Cert-PfxPassword.ps1 的 C# 對應——這支邏輯必須跟 .ps1 版本逐位元組一致，
/// 否則新網頁產的 PFX 在遠端（用 Get-PfxPassword.ps1 算密碼）會解不開。
/// 密碼 = Base64Url(HMAC-SHA256(CERT_PFX_SECRET, 正規化名稱)) 取前 24 碼。
/// </summary>
public sealed partial class PfxPasswordService
{
    [GeneratedRegex(@"\.(pfx|crt|cer|key|ca-bundle|pem|p12)$", RegexOptions.IgnoreCase)]
    private static partial Regex KnownExtensionRegex();

    public static string NormalizeName(string nameOrFile)
    {
        var n = nameOrFile.Trim();
        n = KnownExtensionRegex().Replace(n, "");
        n = n.Trim('_');
        n = n.Replace('_', '.');
        n = n.ToLowerInvariant();
        return n;
    }

    public string ComputePassword(string nameOrFile, string? secret = null)
    {
        secret ??= Environment.GetEnvironmentVariable("CERT_PFX_SECRET");
        if (string.IsNullOrWhiteSpace(secret))
            throw new InvalidOperationException("未設定環境變數 CERT_PFX_SECRET，無法產生 PFX 密碼。請依「操作說明.md」第十一章設定 CERT_PFX_SECRET。");

        var name = NormalizeName(nameOrFile);
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(name));

        var b64 = Convert.ToBase64String(hash)
            .Replace("+", "")
            .Replace("/", "")
            .Replace("=", "");

        return b64[..24];
    }
}
