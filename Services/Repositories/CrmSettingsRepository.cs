using Microsoft.EntityFrameworkCore;
using SslWebConsole.Data;
using SslWebConsole.Data.Entities;

namespace SslWebConsole.Services;

public sealed record CrmSettings(string Cookie, string BaseUrl);

/// <summary>
/// CRM 校對（CRM_Verify）用的 Cookie/BaseUrl，存在同一張 Settings 表（Id=1），但跟
/// NicSrsConfigLoader 管的 nicsrs_config.json 欄位完全獨立——CRM cookie 不寫進
/// nicsrs_config.json（.ps1 腳本用不到，也不該把這種易過期的憑證混進那份設定檔）。
/// </summary>
public sealed class CrmSettingsRepository
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly SecretProtector _protector;

    public CrmSettingsRepository(IDbContextFactory<AppDbContext> dbFactory, SecretProtector protector)
    {
        _dbFactory = dbFactory;
        _protector = protector;
    }

    public CrmSettings Load()
    {
        using var db = _dbFactory.CreateDbContext();
        var e = db.Settings.Find(1);
        if (e is null) return new CrmSettings("", "https://work.eztrust.tw");
        return new CrmSettings(_protector.Unprotect(e.CrmCookieEncrypted), e.CrmBaseUrl);
    }

    public void Save(string cookie, string baseUrl)
    {
        using var db = _dbFactory.CreateDbContext();
        var e = db.Settings.Find(1);
        var isNew = e is null;
        e ??= new SettingsEntity { Id = 1 };

        e.CrmCookieEncrypted = _protector.Protect(cookie);
        e.CrmBaseUrl = string.IsNullOrWhiteSpace(baseUrl) ? "https://work.eztrust.tw" : baseUrl;
        e.UpdatedAt = DateTime.Now;

        if (isNew) db.Settings.Add(e);
        db.SaveChanges();
    }
}
