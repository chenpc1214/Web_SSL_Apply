using Microsoft.EntityFrameworkCore;
using SslWebConsole.Data;

namespace SslWebConsole.Services;

/// <summary>
/// 啟動時跑一次：Domains 表還是空的、但 SSL_List.csv 有資料列時，把 CSV 內容匯入 DB。
/// 只在「資料庫是全新的」情況下動作，避免每次啟動都重複匯入把使用者在網頁上的刪除操作又救回來。
/// </summary>
public sealed class CsvSeedImporter
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly CsvDomainStore _csvStore;
    private readonly DomainRepository _domainRepo;

    public CsvSeedImporter(IDbContextFactory<AppDbContext> dbFactory, CsvDomainStore csvStore, DomainRepository domainRepo)
    {
        _dbFactory = dbFactory;
        _csvStore = csvStore;
        _domainRepo = domainRepo;
    }

    public void ImportIfEmpty()
    {
        using var db = _dbFactory.CreateDbContext();
        if (db.Domains.Any()) return;

        var rows = _csvStore.Load();
        if (rows.Count == 0) return;

        _domainRepo.SaveAll(rows);
    }
}
