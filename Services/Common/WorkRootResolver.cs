namespace SslWebConsole.Services;

/// <summary>
/// 對應 nicsrs_common.ps1 的 Get-WorkRoot：優先序 override > config.WorkRoot > fallback。
/// 解析出的資料夾若不存在會自動建立。
///
/// WorkRoot 設定值代表「整個工作區的根目錄」，實際的網域資料夾一律放在它底下固定名稱
/// 的 <see cref="DomainFolderSubDir"/> 子資料夾——這樣 WorkRoot 底下會固定長出
/// CRT_Preparatory_Area（網域資料夾）、PFX_Storage、Certificates_List.csv 三樣東西，
/// 不管 WorkRoot 本身填什麼路徑都一樣。
/// </summary>
public sealed class WorkRootResolver
{
    public const string DomainFolderSubDir = "CRT_Preparatory_Area";

    private readonly NicSrsConfigLoader _configLoader;

    public WorkRootResolver(NicSrsConfigLoader configLoader)
    {
        _configLoader = configLoader;
    }

    /// <summary>網域資料夾的直接上層（= WorkRoot 設定值 + 固定的 CRT_Preparatory_Area 子資料夾）。</summary>
    public string Resolve(string? overrideWorkRoot = null)
    {
        string root;
        if (!string.IsNullOrWhiteSpace(overrideWorkRoot))
        {
            root = overrideWorkRoot;
        }
        else
        {
            var configuredRoot = _configLoader.Load().WorkRoot;
            root = !string.IsNullOrWhiteSpace(configuredRoot) ? configuredRoot : _configLoader.ScriptsRoot;
        }

        root = Path.Combine(Path.GetFullPath(root), DomainFolderSubDir);
        Directory.CreateDirectory(root);
        return root;
    }

    /// <summary>「<WorkRoot>\CRT_Preparatory_Area\<FolderName>」 網域資料夾路徑（不保證存在）。</summary>
    public string DomainDir(string commonName, string? overrideWorkRoot = null)
    {
        var folderName = DomainFolderNaming.ToFolderName(commonName);
        return Path.Combine(Resolve(overrideWorkRoot), folderName);
    }

    /// <summary>
    /// WorkRoot 設定值本身（不含 CRT_Preparatory_Area）——PFX_Storage、Certificates_List.csv
    /// 這類「集中輸出」放這裡，跟網域工作資料夾分開，避免混在同一層。
    /// </summary>
    public string ResolveOutputRoot(string? overrideWorkRoot = null)
    {
        var root = Resolve(overrideWorkRoot);
        return Directory.GetParent(root)?.FullName ?? root;
    }
}
