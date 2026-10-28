using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using SslWebConsole.Data;
using SslWebConsole.Data.Entities;

namespace SslWebConsole.Services;

/// <summary>
/// 對應原本的 nicsrs_config.json 結構，現在是 DB 讀出來映射成的 DTO——
/// 保持這個形狀不變，讓 WorkRootResolver/NicSrsApiClient/PlaceOrderService/
/// CertCollectService/Settings.razor 完全不用因為換成資料庫而修改。
/// </summary>
public sealed class NicSrsConfig
{
    [JsonPropertyName("ApiBase")]
    public string ApiBase { get; set; } = "";

    [JsonPropertyName("ApiToken")]
    public string ApiToken { get; set; } = "";

    [JsonPropertyName("WorkRoot")]
    public string WorkRoot { get; set; } = "";

    /// <summary>步驟6「封存並清理」的目的地，選填。</summary>
    [JsonPropertyName("ArchiveRoot")]
    public string ArchiveRoot { get; set; } = "";

    [JsonPropertyName("DefaultDcvMethod")]
    public string DefaultDcvMethod { get; set; } = "HTTP_CSR_HASH";

    [JsonPropertyName("PollIntervalSeconds")]
    public int PollIntervalSeconds { get; set; } = 120;

    [JsonPropertyName("PollMaxMinutes")]
    public int PollMaxMinutes { get; set; } = 60;

    /// <summary>各清單頁（/domains、/overview 等）分頁的每頁筆數，共用同一個設定值。</summary>
    [JsonPropertyName("PageSize")]
    public int PageSize { get; set; } = 20;

    [JsonPropertyName("Administrator")]
    public AdministratorInfo Administrator { get; set; } = new();
}

/// <summary>
/// 對應 4_Place_Order.ps1 下單時原封不動轉送給 NicSRS API 的聯絡人區塊。
/// 欄位名稱（含 "organation" 這個原始拼字）必須跟 API 預期的一致，不能「修正」拼字。
/// </summary>
public sealed class AdministratorInfo
{
    [JsonPropertyName("job")] public string Job { get; set; } = "";
    [JsonPropertyName("city")] public string City { get; set; } = "";
    [JsonPropertyName("email")] public string Email { get; set; } = "";
    [JsonPropertyName("state")] public string State { get; set; } = "";
    [JsonPropertyName("mobile")] public string Mobile { get; set; } = "";
    [JsonPropertyName("address")] public string Address { get; set; } = "";
    [JsonPropertyName("country")] public string Country { get; set; } = "";
    [JsonPropertyName("lastName")] public string LastName { get; set; } = "";
    [JsonPropertyName("postCode")] public string PostCode { get; set; } = "";
    [JsonPropertyName("firstName")] public string FirstName { get; set; } = "";
    [JsonPropertyName("organation")] public string Organation { get; set; } = "";
}

/// <summary>
/// 指向「腳本所在資料夾」的根目錄——nicsrs_config.json、SSL_List.csv、sslautomation.db、
/// openssl.exe 呼叫、網域資料夾都以這裡為準。預設是這個 Blazor 專案的上一層（跟 .ps1 同一個資料夾）。
/// </summary>
public sealed class ScriptsRootOptions
{
    public string ScriptsRoot { get; set; } = "";
}

/// <summary>
/// 設定存在 DB 的 Settings 表，是唯一真實來源——網頁不再回寫 nicsrs_config.json（不需要
/// 這份給 .ps1 腳本用的相容檔案了）。DB 裡還沒有資料時（第一次啟動），仍會用既有
/// nicsrs_config.json 的內容當種子資料寫進 DB，不用重填 Token——這只是單次的舊資料匯入，
/// 不代表之後還會繼續寫這個檔案。
/// </summary>
public sealed class NicSrsConfigLoader
{
    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly string _scriptsRoot;
    private readonly string _configPath;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly SecretProtector _protector;

    public NicSrsConfigLoader(
        Microsoft.Extensions.Options.IOptions<ScriptsRootOptions> options,
        IWebHostEnvironment env,
        IDbContextFactory<AppDbContext> dbFactory,
        SecretProtector protector)
    {
        var configured = options.Value.ScriptsRoot;
        _scriptsRoot = string.IsNullOrWhiteSpace(configured)
            ? Path.GetFullPath(Path.Combine(env.ContentRootPath, ".."))
            : Path.GetFullPath(configured);
        _configPath = Path.Combine(_scriptsRoot, "nicsrs_config.json");
        _dbFactory = dbFactory;
        _protector = protector;
    }

    public string ScriptsRoot => _scriptsRoot;

    /// <summary>WorkRoot 設定值本身（不含 CRT_Preparatory_Area），沒設定就退回 ScriptsRoot。</summary>
    private string ResolveOutputRoot(string? configuredWorkRoot)
    {
        var root = !string.IsNullOrWhiteSpace(configuredWorkRoot) ? Path.GetFullPath(configuredWorkRoot) : _scriptsRoot;
        Directory.CreateDirectory(root);
        return root;
    }

    /// <summary>供其他服務（例如 CsvDomainStore）複用，算出「集中輸出」該放哪裡。</summary>
    public string ResolveOutputRoot() => ResolveOutputRoot(Load().WorkRoot);

    /// <summary>讀取設定。不驗證 ApiToken——DNS 檢查/CSR 產生等步驟不需要 API Token。</summary>
    public NicSrsConfig Load()
    {
        using var db = _dbFactory.CreateDbContext();
        var entity = db.Settings.Find(1) ?? SeedFromJsonOrDefault(db);
        return MapToConfig(entity);
    }

    /// <summary>供需要呼叫 NicSRS API 的步驟（下單、輪詢取證）使用，缺 Token 時直接丟例外。</summary>
    public NicSrsConfig LoadRequireApiToken()
    {
        var cfg = Load();
        if (string.IsNullOrWhiteSpace(cfg.ApiToken) || cfg.ApiToken.Contains("貼上"))
            throw new InvalidOperationException("尚未設定 API Token，請至「公用設定」填寫 ApiToken。");
        return cfg;
    }

    /// <summary>存進 DB（唯一真實來源，網頁自己不再讀寫 nicsrs_config.json）。</summary>
    public void Save(NicSrsConfig config)
    {
        using var db = _dbFactory.CreateDbContext();
        var entity = db.Settings.Find(1);
        var isNew = entity is null;
        entity ??= new SettingsEntity { Id = 1 };

        MapToEntity(config, entity);
        entity.UpdatedAt = DateTime.Now;

        if (isNew) db.Settings.Add(entity);
        db.SaveChanges();
    }

    private SettingsEntity SeedFromJsonOrDefault(AppDbContext db)
    {
        NicSrsConfig seed;
        if (File.Exists(_configPath))
        {
            var json = File.ReadAllText(_configPath);
            seed = JsonSerializer.Deserialize<NicSrsConfig>(json, ReadOptions) ?? new NicSrsConfig();
        }
        else
        {
            seed = new NicSrsConfig();
        }

        var entity = new SettingsEntity { Id = 1 };
        MapToEntity(seed, entity);
        entity.UpdatedAt = DateTime.Now;
        db.Settings.Add(entity);
        db.SaveChanges();
        return entity;
    }

    private NicSrsConfig MapToConfig(SettingsEntity e) => new()
    {
        ApiBase = e.ApiBase,
        ApiToken = _protector.Unprotect(e.ApiTokenEncrypted),
        WorkRoot = e.WorkRoot,
        ArchiveRoot = e.ArchiveRoot,
        DefaultDcvMethod = e.DefaultDcvMethod,
        PollIntervalSeconds = e.PollIntervalSeconds,
        PollMaxMinutes = e.PollMaxMinutes,
        PageSize = e.PageSize > 0 ? e.PageSize : 20,
        Administrator = new AdministratorInfo
        {
            FirstName = e.AdminFirstName,
            LastName = e.AdminLastName,
            Job = e.AdminJob,
            Organation = e.AdminOrganization,
            Email = e.AdminEmail,
            Mobile = e.AdminMobile,
            Country = e.AdminCountry,
            State = e.AdminState,
            City = e.AdminCity,
            PostCode = e.AdminPostCode,
            Address = e.AdminAddress,
        },
    };

    private void MapToEntity(NicSrsConfig cfg, SettingsEntity e)
    {
        e.ApiBase = cfg.ApiBase;
        e.ApiTokenEncrypted = _protector.Protect(cfg.ApiToken);
        e.WorkRoot = cfg.WorkRoot;
        e.ArchiveRoot = (cfg.ArchiveRoot ?? "").Trim();
        e.DefaultDcvMethod = cfg.DefaultDcvMethod;
        e.PollIntervalSeconds = cfg.PollIntervalSeconds;
        e.PollMaxMinutes = cfg.PollMaxMinutes;
        e.PageSize = cfg.PageSize > 0 ? cfg.PageSize : 20;
        e.AdminFirstName = cfg.Administrator.FirstName;
        e.AdminLastName = cfg.Administrator.LastName;
        e.AdminJob = cfg.Administrator.Job;
        e.AdminOrganization = cfg.Administrator.Organation;
        e.AdminEmail = cfg.Administrator.Email;
        e.AdminMobile = cfg.Administrator.Mobile;
        e.AdminCountry = cfg.Administrator.Country;
        e.AdminState = cfg.Administrator.State;
        e.AdminCity = cfg.Administrator.City;
        e.AdminPostCode = cfg.Administrator.PostCode;
        e.AdminAddress = cfg.Administrator.Address;
    }
}
