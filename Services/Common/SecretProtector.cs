using Microsoft.AspNetCore.DataProtection;

namespace SslWebConsole.Services;

/// <summary>
/// 包 ASP.NET Core 內建的 Data Protection API（免額外套件），用來加密存進 DB 的
/// ApiToken、FtpPass 這類敏感欄位。金鑰預設存在使用者設定檔底下（%LOCALAPPDATA%），
/// 跟本機/本帳號綁定——單機個人工具用預設值即可，不用額外配置金鑰儲存位置。
/// </summary>
public sealed class SecretProtector
{
    private readonly IDataProtector _protector;

    public SecretProtector(IDataProtectionProvider provider)
    {
        _protector = provider.CreateProtector("SslWebConsole.Secrets.v1");
    }

    public string Protect(string? plainText) =>
        string.IsNullOrEmpty(plainText) ? "" : _protector.Protect(plainText);

    public string Unprotect(string? cipherText)
    {
        if (string.IsNullOrEmpty(cipherText)) return "";
        try
        {
            return _protector.Unprotect(cipherText);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // 密文格式不對（例如手動塞了明碼進 DB）：安全起見回空字串，不讓例外炸掉整頁。
            return "";
        }
    }
}
