namespace SslWebConsole.Services;

/// <summary>
/// FolderName 正規化規則，對應所有 .ps1 腳本共用的慣例：CommonName 的 "*" 與 "." 一律換成 "_"。
/// </summary>
public static class DomainFolderNaming
{
    public static string ToFolderName(string commonName) =>
        commonName.Trim().Replace("*", "_").Replace(".", "_");
}
