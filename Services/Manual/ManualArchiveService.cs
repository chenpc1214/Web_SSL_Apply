namespace SslWebConsole.Services;

public sealed record ManualArchiveResult(ArchiveResult Archive, List<string> ArchivedNames, int MovedToCompletedCsv, string? CsvError);

/// <summary>
/// 人工模式的「封存並清理」。搬資料夾、刪 PFX 複本直接沿用 CertArchiveService.Archive（那段本來就
/// 不碰資料庫），這裡只決定要搬哪些：這個品牌 CSV 裡的網域、資料夾裡有 .pfx 的就封存。
/// 沒有訂單狀態可以參考（人工模式不存資料庫），所以只看 .pfx。
/// PFX_Storage 只刪這次封存的網域的 PFX——其他品牌的 PFX 也在同一個資料夾，不能一起清掉。
/// 封存成功的網域接著從品牌 CSV 搬到「&lt;品牌&gt;_完成.csv」。
/// </summary>
public sealed class ManualArchiveService
{
    private readonly ManualWorkspace _workspace;
    private readonly BrandCsvStore _csvStore;
    private readonly CertArchiveService _archive;

    public ManualArchiveService(ManualWorkspace workspace, BrandCsvStore csvStore, CertArchiveService archive)
    {
        _workspace = workspace;
        _csvStore = csvStore;
        _archive = archive;
    }

    public ArchivePlan Plan(string brand)
    {
        var source = _workspace.DomainRoot;
        var pfxStorage = _workspace.PfxStorage;

        var ready = new List<string>();
        var skipped = new List<ArchiveSkip>();
        foreach (var row in _csvStore.Load(brand).Rows)
        {
            var dir = _workspace.DomainDir(row.CommonName);
            if (!Directory.Exists(dir)) continue; // 還沒產生 CSR 的不算在這一輪
            if (Directory.EnumerateFiles(dir, "*.pfx").Any()) ready.Add(dir);
            else skipped.Add(new ArchiveSkip(dir, "還沒打包 PFX"));
        }

        var pfxFiles = ready
            .Select(d => Path.Combine(pfxStorage, $"{Path.GetFileName(d)}.pfx"))
            .Where(File.Exists)
            .ToList();

        return new ArchivePlan(source, _archive.ArchiveRoot, pfxStorage, ready, skipped, pfxFiles);
    }

    public ManualArchiveResult Archive(string brand, ArchivePlan plan)
    {
        var result = _archive.Archive(plan);

        // 搬走了（來源資料夾不在了）才算封存成功；搬失敗的原資料夾還在，CSV 也不動
        var archivedFolders = plan.Ready
            .Where(d => !Directory.Exists(d))
            .Select(f => Path.GetFileName(f))
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var archivedNames = _csvStore.Load(brand).Rows
            .Select(r => r.CommonName.Trim())
            .Where(n => archivedFolders.Contains(DomainFolderNaming.ToFolderName(n)))
            .ToList();

        try
        {
            var moved = _csvStore.MoveToCompleted(brand, archivedNames);
            return new ManualArchiveResult(result, archivedNames, moved, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BrandCsvChangedException)
        {
            return new ManualArchiveResult(result, archivedNames, 0,
                $"資料夾已經封存，但 {brand}.csv 沒有更新：{ex.Message}。請關閉 Excel 後，手動把這幾筆搬到 {brand}{ManualWorkspace.CompletedSuffix}.csv");
        }
    }
}
