using Microsoft.EntityFrameworkCore;
using SslWebConsole.Data;
using SslWebConsole.Data.Entities;

namespace SslWebConsole.Services;

public sealed record CertFileSummary(DateTime GeneratedAt);

/// <summary>
/// 供 PfxService 打包 PFX 成功後寫進 CertificateFiles 表；找不到對應的 Domain 列就跳過
/// （回 false），不擋原本的檔案流程——DB 紀錄是加值，不是打包能不能動作的前提。
/// ★ 「這個網域有一筆紀錄」本身就代表「PFX 打包了」——/webconfig-upload、/overview 只看
/// 有沒有紀錄，GeneratedAt 給畫面顯示完成日期/排序用。刻意寫進 DB 就是要不受「打包完就把
/// 資料夾搬去封存」這個習慣影響；也因為會搬走，任何指向檔案的路徑（PfxPath/CertPath/
/// KeyPath/BundlePath）都會失真，且從沒被讀回來用過，一律不存。資料庫裡舊的 PfxPath 欄位
/// 留著不動（EnsureCreated 不會改既有表結構），EF 不再讀寫它。
/// </summary>
public sealed class CertificateFileRepository
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;

    public CertificateFileRepository(IDbContextFactory<AppDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public bool TryUpsert(string commonName)
    {
        try
        {
            using var db = _dbFactory.CreateDbContext();
            var domain = DomainRepository.FindByName(db, commonName);
            if (domain is null) return false;

            var entity = db.CertificateFiles.FirstOrDefault(f => f.DomainId == domain.Id);
            var isNew = entity is null;
            entity ??= new CertificateFileEntity { DomainId = domain.Id };

            entity.GeneratedAt = DateTime.Now;

            if (isNew) db.CertificateFiles.Add(entity);
            db.SaveChanges();
            return true;
        }
        catch (Exception)
        {
            // DB 是加值紀錄，寫入失敗不影響掃描/打包本身已經成功這件事。
            return false;
        }
    }

    /// <summary>唯讀查詢：這個網域的 PFX 打包紀錄（給編輯頁用），還沒打包或查不到就回 null。</summary>
    public CertFileSummary? GetByDomain(string commonName)
    {
        try
        {
            using var db = _dbFactory.CreateDbContext();
            var domain = DomainRepository.FindByName(db, commonName);
            if (domain is null) return null;

            var entity = db.CertificateFiles.AsNoTracking().FirstOrDefault(f => f.DomainId == domain.Id);
            return entity is null ? null : new CertFileSummary(entity.GeneratedAt);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// 唯讀查詢：一次撈回全部網域的 PFX 打包紀錄，key 是 CommonName（不分大小寫）。
    /// 整份清單逐筆呼叫 GetByDomain 會每筆各開一個 DbContext；這裡兩個查詢撈完在記憶體對應。
    /// 讀不到就回空字典，不擋畫面。
    /// </summary>
    public Dictionary<string, CertFileSummary> GetAllByDomain()
    {
        var map = new Dictionary<string, CertFileSummary>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var db = _dbFactory.CreateDbContext();
            var domains = db.Domains.AsNoTracking().ToList();
            var filesByDomainId = db.CertificateFiles.AsNoTracking().ToList()
                .GroupBy(f => f.DomainId)
                .ToDictionary(g => g.Key, g => g.First());

            foreach (var d in domains)
            {
                if (!filesByDomainId.TryGetValue(d.Id, out var file)) continue;
                map[d.CommonName.Trim()] = new CertFileSummary(file.GeneratedAt);
            }
        }
        catch (Exception)
        {
            // DB 是加值紀錄，讀不到就當作還沒打包，不擋畫面。
        }
        return map;
    }
}
