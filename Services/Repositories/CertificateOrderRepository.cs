using Microsoft.EntityFrameworkCore;
using SslWebConsole.Data;
using SslWebConsole.Data.Entities;

namespace SslWebConsole.Services;

public sealed record OrderSummary(string? CertId, string Status, DateTime PlacedAt, DateTime? IssuedAt, DateTime? CertExpiresAt);

/// <summary>
/// 供 PlaceOrderService（下單成功時新增一列）與 CertCollectService（輪詢到 COMPLETE/FAILED
/// 時更新對應列）共用。找不到對應網域/訂單就靜默略過；DB 寫入若失敗也只吞掉例外——
/// DB 紀錄是加值，不是下單/取證能不能動作的前提，原本的 .certid/.order.json 檔案流程
/// 完全不受影響，不該因為 DB 出狀況就讓真正的下單/取證流程跟著失敗。
/// </summary>
public sealed class CertificateOrderRepository
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;

    public CertificateOrderRepository(IDbContextFactory<AppDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public void RecordPlaced(string commonName, string? certId)
    {
        TrySafe(() =>
        {
            using var db = _dbFactory.CreateDbContext();
            var domain = FindDomain(db, commonName);
            if (domain is null) return;

            db.CertificateOrders.Add(new CertificateOrderEntity
            {
                DomainId = domain.Id,
                CertId = certId,
                Status = "Pending",
                PlacedAt = DateTime.Now,
            });
            db.SaveChanges();
        });
    }

    public void RecordIssued(string commonName, string? certId, DateTime? certExpiresAt)
    {
        TrySafe(() =>
        {
            using var db = _dbFactory.CreateDbContext();
            var order = FindLatestOrder(db, commonName, certId);
            if (order is null) return;
            order.Status = "Issued";
            order.IssuedAt = DateTime.Now;
            order.CertExpiresAt = certExpiresAt;
            db.SaveChanges();
        });
    }

    /// <summary>
    /// 免費重簽／更新憑證（/ssl/replace）成功後呼叫：把該網域最新一筆訂單改回 Pending
    /// （代表要重新走一次 DCV 驗證＋等簽發），更新 CertId。不動 PlacedAt——原始購買紀錄
    /// 不變，這不是一筆新訂單。
    /// ★ 找不到既有訂單列（例如這網域本來就是終端機 .ps1 下的單，我們資料庫從沒記錄過）
    /// 時，改成新增一筆——不能像原本那樣直接放棄，不然重簽明明成功了，資料庫卻完全沒有
    /// 紀錄，導致 /webconfig-upload、/collect-cert 永遠抓不到這筆資料。
    /// </summary>
    public void RecordReissued(string commonName, string? newCertId)
    {
        TrySafe(() =>
        {
            using var db = _dbFactory.CreateDbContext();
            var order = FindLatestOrder(db, commonName, certId: null);
            if (order is not null)
            {
                order.Status = "Pending";
                if (!string.IsNullOrWhiteSpace(newCertId)) order.CertId = newCertId;
                db.SaveChanges();
                return;
            }

            var domain = FindDomain(db, commonName);
            if (domain is null) return;

            db.CertificateOrders.Add(new CertificateOrderEntity
            {
                DomainId = domain.Id,
                CertId = newCertId,
                Status = "Pending",
                PlacedAt = DateTime.Now,
            });
            db.SaveChanges();
        });
    }

    public void RecordFailed(string commonName, string? certId)
    {
        TrySafe(() =>
        {
            using var db = _dbFactory.CreateDbContext();
            var order = FindLatestOrder(db, commonName, certId);
            if (order is null) return;
            order.Status = "Failed";
            db.SaveChanges();
        });
    }

    /// <summary>唯讀查詢：這個網域最近一筆訂單摘要（給總覽頁用），查不到就回 null。</summary>
    public OrderSummary? GetLatest(string commonName)
    {
        try
        {
            using var db = _dbFactory.CreateDbContext();
            var order = FindLatestOrder(db, commonName, certId: null);
            return order is null
                ? null
                : new OrderSummary(order.CertId, order.Status, order.PlacedAt, order.IssuedAt, order.CertExpiresAt);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// 唯讀查詢：一次撈回「每個網域最新一筆訂單」，key 是 CommonName（不分大小寫）。
    /// 總覽頁一次要顯示整份清單，逐筆呼叫 GetLatest 會每筆各開一個 DbContext、各整表撈一次
    /// Domains；這裡兩個查詢撈完就在記憶體分組。讀不到就回空字典（跟 GetLatest 一樣不擋畫面）。
    /// </summary>
    public Dictionary<string, OrderSummary> GetLatestAll()
    {
        var map = new Dictionary<string, OrderSummary>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var db = _dbFactory.CreateDbContext();
            var domains = db.Domains.AsNoTracking().ToList();
            var latestByDomainId = db.CertificateOrders.AsNoTracking().ToList()
                .GroupBy(o => o.DomainId)
                .ToDictionary(g => g.Key, g => g.MaxBy(o => o.PlacedAt)!);

            foreach (var d in domains)
            {
                if (!latestByDomainId.TryGetValue(d.Id, out var order)) continue;
                map[d.CommonName.Trim()] = new OrderSummary(order.CertId, order.Status, order.PlacedAt, order.IssuedAt, order.CertExpiresAt);
            }
        }
        catch (Exception)
        {
            // DB 是加值紀錄，讀不到就當作沒有訂單資料，不擋畫面。
        }
        return map;
    }

    private static void TrySafe(Action action)
    {
        try { action(); }
        catch (Exception) { /* DB 是加值紀錄，寫入失敗不影響主流程 */ }
    }

    private static DomainEntity? FindDomain(AppDbContext db, string commonName) =>
        DomainRepository.FindByName(db, commonName);

    private static CertificateOrderEntity? FindLatestOrder(AppDbContext db, string commonName, string? certId)
    {
        var domain = FindDomain(db, commonName);
        if (domain is null) return null;

        var orders = db.CertificateOrders.Where(o => o.DomainId == domain.Id).ToList();
        if (!string.IsNullOrWhiteSpace(certId))
        {
            var byCertId = orders.Where(o => o.CertId == certId).MaxBy(o => o.PlacedAt);
            if (byCertId is not null) return byCertId;
        }
        return orders.MaxBy(o => o.PlacedAt);
    }
}
