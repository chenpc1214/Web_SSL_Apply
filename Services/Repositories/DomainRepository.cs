using Microsoft.EntityFrameworkCore;
using SslWebConsole.Data;
using SslWebConsole.Data.Entities;
using SslWebConsole.Models;

namespace SslWebConsole.Services;

/// <summary>
/// 網域清單存在 DB 的 Domains 表（唯一真實來源）。SaveAll 只寫資料庫，不會自動動到
/// SSL_List.csv——CSV 是給終端機 .ps1 腳本讀的相容產物，要更新請明確呼叫 ExportCsv()
/// （對應網頁上的「匯出 CSV」按鈕），避免每次編輯都把 FtpPass 明碼寫一次到磁碟。
/// 對外一律用既有的 DomainRecord 當 DTO——DnsCheckService/CsrService/PlaceOrderService/
/// ValidationUploadService/WebConfigPatchService 全部只認 DomainRecord，換成資料庫後
/// 這些服務完全不用改。
/// </summary>
public sealed class DomainRepository
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly SecretProtector _protector;
    private readonly CsvDomainStore _csvStore;

    public DomainRepository(IDbContextFactory<AppDbContext> dbFactory, SecretProtector protector, CsvDomainStore csvStore)
    {
        _dbFactory = dbFactory;
        _protector = protector;
        _csvStore = csvStore;
    }

    public List<DomainRecord> LoadAll()
    {
        using var db = _dbFactory.CreateDbContext();
        return db.Domains
            .OrderBy(d => d.CommonName)
            .AsNoTracking()
            .ToList()
            .Select(ToRecord)
            .ToList();
    }

    /// <summary>
    /// 純 upsert 語意——只新增/更新傳入的列，絕不刪除資料庫裡其他既有的列。
    /// ★ 之前是「清單外一律砍」的整表覆寫語意，但只要呼叫端傳入的是「畫面上目前顯示的
    /// 子集合」（例如 /domains 過濾成只顯示尚未下單的網域），整表覆寫就會把畫面上看不到、
    /// 但其實還在資料庫裡的資料（例如已下單的網域）整批刪掉。刪除改成明確呼叫 Delete()，
    /// 不再是「儲存時track清單裡沒有就砍」的隱性副作用。
    /// </summary>
    public void SaveAll(IEnumerable<DomainRecord> rows)
    {
        var list = rows.Where(r => !string.IsNullOrWhiteSpace(r.CommonName)).ToList();

        using var db = _dbFactory.CreateDbContext();
        var existing = db.Domains.ToList();

        var now = DateTime.Now;
        foreach (var row in list)
        {
            var name = row.CommonName.Trim();
            var entity = existing.FirstOrDefault(e => string.Equals(e.CommonName, name, StringComparison.OrdinalIgnoreCase));
            var isNew = entity is null;
            entity ??= new DomainEntity { CommonName = name, CreatedAt = now };

            entity.ExternalId = row.Id;
            entity.CommonName = name;
            entity.Organization = row.Organization;
            entity.Ip = row.Ip;
            entity.FtpUser = row.FtpUser;
            entity.FtpPassEncrypted = _protector.Protect(row.FtpPass);
            entity.FtpFolder = row.FtpFolder;
            entity.ProductCode = row.ProductCode;
            entity.Years = row.Years;
            entity.Locality = row.Locality;
            entity.Remark = row.Remark;
            entity.UpdatedAt = now;

            if (isNew) db.Domains.Add(entity);
        }

        db.SaveChanges();
    }

    /// <summary>明確、立即刪除單一網域（見 SaveAll 上的說明——刪除不再是儲存時的隱性副作用）。
    /// 連同這個網域的 CertificateOrders/CertificateFiles 一起刪——表之間沒有設外鍵，不手動刪
    /// 就會留下對不到任何網域的孤兒資料。</summary>
    public void Delete(string commonName)
    {
        using var db = _dbFactory.CreateDbContext();
        var entity = FindByName(db, commonName);
        if (entity is not null)
        {
            db.CertificateOrders.RemoveRange(db.CertificateOrders.Where(o => o.DomainId == entity.Id));
            db.CertificateFiles.RemoveRange(db.CertificateFiles.Where(f => f.DomainId == entity.Id));
            db.Domains.Remove(entity);
            db.SaveChanges();
        }
    }

    /// <summary>用 SQL 找單一網域（不分大小寫），不把整張 Domains 表撈進記憶體再比對。</summary>
    internal static DomainEntity? FindByName(AppDbContext db, string commonName)
    {
        var name = commonName.Trim().ToLower();
        return db.Domains.FirstOrDefault(e => e.CommonName.ToLower() == name);
    }

    public string CsvPath => _csvStore.CsvPath;

    /// <summary>明確匯出 SSL_List.csv 給 .ps1 腳本讀——直接用傳進來的原始資料（含明碼 FtpPass）。</summary>
    public void ExportCsv(IEnumerable<DomainRecord> rows)
    {
        _csvStore.Save(rows.Where(r => !string.IsNullOrWhiteSpace(r.CommonName)));
    }

    /// <summary>讀回 SSL_List.csv（跟 ExportCsv 寫的是同一個固定檔案）——只回傳讀到的內容，
    /// 不碰資料庫；呼叫端自己決定怎麼跟畫面上現有的列合併，合併完仍要按「儲存」才會真的寫進 DB。</summary>
    public List<DomainRecord> ImportCsv() => _csvStore.Load();

    /// <summary>這個網域是不是「已完成但正在準備續約/重簽」——存資料庫、不是記憶體，網站
    /// 重啟也不會忘記。查不到這個網域就回 false（例如剛好被刪除了）。</summary>
    public bool IsPendingRenewal(string commonName)
    {
        using var db = _dbFactory.CreateDbContext();
        return FindByName(db, commonName)?.PendingRenewal ?? false;
    }

    /// <summary>
    /// 一次撈回全部網域的「正在準備續約/重簽」旗標，key 是 CommonName（不分大小寫）。
    /// 總覽頁每畫一次要對每個網域問好幾次這個狀態，逐筆呼叫 IsPendingRenewal 等於「網域
    /// 數量的平方」次整表載入（它自己也是整表撈進記憶體再找），網域一多就明顯變慢。
    /// </summary>
    public Dictionary<string, bool> LoadPendingRenewalMap()
    {
        using var db = _dbFactory.CreateDbContext();
        var map = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in db.Domains.AsNoTracking().ToList())
            map[e.CommonName.Trim()] = e.PendingRenewal;
        return map;
    }

    /// <summary>設定/清除「正在準備續約/重簽」標記。找不到這個網域就靜默不做事（可能剛好被刪除）。</summary>
    public void SetPendingRenewal(string commonName, bool value)
    {
        using var db = _dbFactory.CreateDbContext();
        var entity = FindByName(db, commonName);
        if (entity is null) return;
        entity.PendingRenewal = value;
        db.SaveChanges();
    }

    /// <summary>一次撈回全部「暫緩中」的網域跟標記時間，key 是 CommonName（不分大小寫）。</summary>
    public Dictionary<string, DateTime> LoadHoldMap()
    {
        using var db = _dbFactory.CreateDbContext();
        var map = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in db.Domains.AsNoTracking().Where(e => e.OnHoldSince != null).ToList())
            map[e.CommonName.Trim()] = e.OnHoldSince!.Value;
        return map;
    }

    /// <summary>設定/取消「暫緩」。重複設定不會改掉原本的標記時間。找不到網域就靜默不做事。</summary>
    public void SetHold(string commonName, bool onHold)
    {
        using var db = _dbFactory.CreateDbContext();
        var entity = FindByName(db, commonName);
        if (entity is null) return;
        entity.OnHoldSince = onHold ? entity.OnHoldSince ?? DateTime.Now : null;
        db.SaveChanges();
    }

    /// <summary>一次清掉全部「正在準備續約/重簽」標記——總覽「清除全部勾選」、或按「新網域」
    /// 按鈕語意上要完全空白開始時用。</summary>
    public void ClearAllPendingRenewal()
    {
        using var db = _dbFactory.CreateDbContext();
        var flagged = db.Domains.Where(e => e.PendingRenewal).ToList();
        foreach (var e in flagged) e.PendingRenewal = false;
        db.SaveChanges();
    }

    private DomainRecord ToRecord(DomainEntity e) => new()
    {
        Id = e.ExternalId,
        CommonName = e.CommonName,
        Organization = e.Organization,
        Ip = e.Ip,
        FtpUser = e.FtpUser,
        FtpPass = _protector.Unprotect(e.FtpPassEncrypted),
        FtpFolder = e.FtpFolder,
        ProductCode = e.ProductCode,
        Years = e.Years,
        Locality = e.Locality,
        Remark = e.Remark,
    };
}
