namespace SslWebConsole.Services;

/// <summary>
/// 人工模式（其他憑證商）的資料夾配置。公用設定的「其他憑證商工作區」（例如 D:\test）底下固定長出：
///   BrandCsv\                          品牌 CSV（A_Brand.csv、B_Brand.csv…），使用者自己放
///   Other_Brand_CRT_Preparatory_Area\  網域資料夾（.key / CSR / 驗證檔 / 憑證 / PFX）
///   ZipCrt\                            憑證商給的 ZIP，使用者自己放
///   PFX_Storage\                       打包好的 PFX 集中一份
/// 人工模式不寫資料庫：網域資料在品牌 CSV，做到哪一步看網域資料夾裡有哪些檔案。
/// 封存跟 NicSRS 用同一個封存資料夾（公用設定的 ArchiveRoot）。
/// </summary>
public sealed class ManualWorkspace
{
    public const string BrandCsvSubDir = "BrandCsv";
    public const string ZipSubDir = "ZipCrt";
    public const string PfxSubDir = "PFX_Storage";

    /// <summary>封存時把完成的網域搬到「&lt;品牌&gt;_完成.csv」，列品牌清單時要排除。</summary>
    public const string CompletedSuffix = "_完成";

    private readonly NicSrsConfigLoader _configLoader;

    public ManualWorkspace(NicSrsConfigLoader configLoader) => _configLoader = configLoader;

    // 每一列都會問資料夾路徑（畫面重繪一次就幾十次），NicSrsConfigLoader.Load() 每次都查資料庫、
    // 還會解密 ApiToken，所以記住幾秒。改了公用設定最多幾秒後生效。
    private static readonly TimeSpan RootCacheTtl = TimeSpan.FromSeconds(5);
    private readonly object _rootGate = new();
    private string _cachedRoot = "";
    private DateTime _cachedAtUtc = DateTime.MinValue;

    public string Root
    {
        get
        {
            lock (_rootGate)
            {
                if (DateTime.UtcNow - _cachedAtUtc > RootCacheTtl)
                {
                    _cachedRoot = (_configLoader.Load().ManualWorkRoot ?? "").Trim();
                    _cachedAtUtc = DateTime.UtcNow;
                }
                return _cachedRoot;
            }
        }
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Root);

    /// <summary>網域資料夾的上層。傳給 CsrService / ValidationUploadService / WebConfigPatchService /
    /// PfxService 的 workRootOverride 用——WorkRootResolver 看到這個名稱就不會再往下加 CRT_Preparatory_Area。</summary>
    public string DomainRoot => Path.Combine(RequireRoot(), WorkRootResolver.ManualDomainFolderSubDir);

    public string BrandCsvDir => Path.Combine(RequireRoot(), BrandCsvSubDir);
    public string ZipDir => Path.Combine(RequireRoot(), ZipSubDir);
    public string PfxStorage => Path.Combine(RequireRoot(), PfxSubDir);

    public string DomainDir(string commonName) => Path.Combine(DomainRoot, DomainFolderNaming.ToFolderName(commonName));

    public string BrandCsvPath(string brand) => Path.Combine(BrandCsvDir, $"{brand}.csv");

    /// <summary>四個固定資料夾都建好（已存在就不動）。</summary>
    public void EnsureFolders()
    {
        Directory.CreateDirectory(BrandCsvDir);
        Directory.CreateDirectory(DomainRoot);
        Directory.CreateDirectory(ZipDir);
        Directory.CreateDirectory(PfxStorage);
    }

    /// <summary>BrandCsv 底下的品牌名稱（檔名去掉 .csv），不含「_完成」那種封存紀錄檔。</summary>
    public List<string> ListBrands()
    {
        if (!IsConfigured || !Directory.Exists(BrandCsvDir)) return [];
        return Directory.GetFiles(BrandCsvDir, "*.csv")
            .Select(f => Path.GetFileNameWithoutExtension(f))
            .OfType<string>()
            .Where(n => !n.EndsWith(CompletedSuffix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public bool BrandExists(string? brand) =>
        !string.IsNullOrWhiteSpace(brand) && ListBrands().Contains(brand, StringComparer.OrdinalIgnoreCase);

    private string RequireRoot()
    {
        var root = Root;
        if (string.IsNullOrWhiteSpace(root))
            throw new InvalidOperationException("尚未設定「其他憑證商工作區」，請先到公用設定填寫。");
        return Path.GetFullPath(root);
    }
}
