namespace SslWebConsole.Services;

/// <summary>
/// FTP 遠端路徑正規化共用工具。實際 WinSCP 連線操作已改走 <see cref="WinScpBridgeClient"/>
/// （PowerShell 橋接），詳見該類別註解。
/// </summary>
public static class WinScpSessionFactory
{
    public static string NormalizeRemoteDir(string folder)
    {
        var f = folder.Replace('\\', '/');
        if (!f.StartsWith('/')) f = "/" + f;
        if (!f.EndsWith('/')) f += "/";
        return f;
    }
}
