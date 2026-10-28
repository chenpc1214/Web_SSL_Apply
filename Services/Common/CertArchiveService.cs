namespace SslWebConsole.Services;

/// <summary>這次不封存的網域資料夾跟原因。</summary>
public sealed record ArchiveSkip(string Dir, string Reason);

/// <param name="Ready">已打包 PFX、這次會封存的網域資料夾。</param>
/// <param name="Skipped">這次不封存的網域資料夾——還在流程中，搬走的話私鑰不在原處，憑證簽發後就打包不了。</param>
public sealed record ArchivePlan(string SourceRoot, string ArchiveRoot, string PfxStorage, List<string> Ready, List<ArchiveSkip> Skipped, List<string> PfxFiles);

public sealed record ArchiveResult(int Moved, int Failed, int PfxDeleted, List<string> Log);

/// <summary>
/// 步驟6「封存並清理」：一輪做完後，把 CRT_Preparatory_Area 裡已打包 PFX 的網域資料夾搬到
/// 封存資料夾（公用設定的 ArchiveRoot），再清掉 PFX_Storage 裡的 PFX。
/// ★ PFX_Storage 的 PFX 只是複本（PfxService 打包時從網域資料夾複製過去），正本跟著網域
/// 資料夾一起封存，所以清掉複本不會遺失檔案。封存失敗的網域保留它的複本。
/// ★ 資料庫的訂單、PFX 打包紀錄不動——總覽頁本來就是看資料庫判斷完成，不受資料夾搬走影響。
/// </summary>
public sealed class CertArchiveService
{
    private readonly WorkRootResolver _workRoot;
    private readonly NicSrsConfigLoader _configLoader;
    private readonly DomainRepository _domainRepo;
    private readonly CertificateOrderRepository _orderRepo;

    public CertArchiveService(WorkRootResolver workRoot, NicSrsConfigLoader configLoader,
        DomainRepository domainRepo, CertificateOrderRepository orderRepo)
    {
        _workRoot = workRoot;
        _configLoader = configLoader;
        _domainRepo = domainRepo;
        _orderRepo = orderRepo;
    }

    public string ArchiveRoot => (_configLoader.Load().ArchiveRoot ?? "").Trim();

    /// <summary>
    /// 只看不動：列出這次會封存、會跳過的資料夾，以及會刪除的 PFX，給確認視窗用。
    /// 封存條件：沒有暫緩、資料夾裡有 .pfx，而且資料庫的訂單是 Issued。只看 .pfx 不夠——
    /// 去年做完沒封存、今年又在同一個資料夾重簽的網域，會留著去年的 .pfx，今年的重簽卻還在等簽發。
    /// 資料庫完全沒有訂單紀錄的（終端機 .ps1 時代的網域）維持只看 .pfx。
    /// </summary>
    public ArchivePlan Plan()
    {
        var source = _workRoot.Resolve();
        var pfxStorage = Path.Combine(_workRoot.ResolveOutputRoot(), "PFX_Storage");

        // 資料夾名稱 -> 訂單狀態／是否暫緩（資料夾名稱由 CommonName 換算，見 DomainFolderNaming）
        var statusByFolder = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (commonName, order) in _orderRepo.GetLatestAll())
            statusByFolder[DomainFolderNaming.ToFolderName(commonName)] = order.Status;
        var heldFolders = _domainRepo.LoadHoldMap().Keys
            .Select(DomainFolderNaming.ToFolderName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var ready = new List<string>();
        var skipped = new List<ArchiveSkip>();
        foreach (var dir in Directory.GetDirectories(source).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(dir);
            if (heldFolders.Contains(name))
                skipped.Add(new ArchiveSkip(dir, "暫緩中"));
            else if (!Directory.EnumerateFiles(dir, "*.pfx").Any())
                skipped.Add(new ArchiveSkip(dir, "還沒打包 PFX"));
            else if (statusByFolder.TryGetValue(name, out var status) && status != "Issued")
                skipped.Add(new ArchiveSkip(dir, $"訂單狀態是 {status}，資料夾裡的 PFX 是舊的"));
            else
                ready.Add(dir);
        }

        var pfxFiles = Directory.Exists(pfxStorage)
            ? Directory.GetFiles(pfxStorage, "*.pfx").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList()
            : new List<string>();

        return new ArchivePlan(source, ArchiveRoot, pfxStorage, ready, skipped, pfxFiles);
    }

    public ArchiveResult Archive(ArchivePlan plan)
    {
        if (string.IsNullOrWhiteSpace(plan.ArchiveRoot))
            throw new InvalidOperationException("尚未設定封存資料夾，請先到公用設定填寫。");

        Directory.CreateDirectory(plan.ArchiveRoot);
        var log = new List<string>();
        var failedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int moved = 0, failed = 0, pfxDeleted = 0;
        var now = DateTime.Now;

        foreach (var dir in plan.Ready)
        {
            var name = Path.GetFileName(dir);
            try
            {
                var dest = UniqueDestination(plan.ArchiveRoot, name, now);
                MoveDirectory(dir, dest);
                moved++;
                var destName = Path.GetFileName(dest);
                log.Add(destName == name
                    ? $"已封存 {name}"
                    : $"已封存 {name}（封存資料夾已有同名資料夾，改名為 {destName}）");
            }
            catch (Exception ex)
            {
                failed++;
                failedNames.Add(name);
                log.Add($"封存失敗 {name}：{ex.Message}（原資料夾保留在原處）");
            }
        }

        foreach (var skip in plan.Skipped)
            log.Add($"略過 {Path.GetFileName(skip.Dir)}：{skip.Reason}，留在原處");

        foreach (var file in plan.PfxFiles)
        {
            var baseName = Path.GetFileNameWithoutExtension(file);
            if (failedNames.Contains(baseName))
            {
                log.Add($"保留 {Path.GetFileName(file)}：對應的網域資料夾封存失敗");
                continue;
            }
            try
            {
                File.Delete(file);
                pfxDeleted++;
            }
            catch (Exception ex)
            {
                log.Add($"刪除 {Path.GetFileName(file)} 失敗：{ex.Message}");
            }
        }

        return new ArchiveResult(moved, failed, pfxDeleted, log);
    }

    /// <summary>封存資料夾裡已經有同名的（例如去年同一個網域的資料夾）就在名稱後面加日期，
    /// 同一天又封存一次再加時間，絕不覆蓋舊的。</summary>
    private static string UniqueDestination(string root, string name, DateTime now)
    {
        var dest = Path.Combine(root, name);
        if (!Directory.Exists(dest)) return dest;

        dest = Path.Combine(root, $"{name}_{now:yyyyMMdd}");
        if (!Directory.Exists(dest)) return dest;

        return Path.Combine(root, $"{name}_{now:yyyyMMdd_HHmmss}");
    }

    /// <summary>同一顆磁碟直接 Directory.Move。跨磁碟（例如 D: 搬到 C:）Directory.Move 不支援，
    /// 改成先完整複製、核對每個檔案都在且大小一致才刪除來源；中途失敗就清掉複製到一半的目的地，
    /// 來源原封不動。</summary>
    private static void MoveDirectory(string source, string dest)
    {
        var sameVolume = string.Equals(
            Path.GetPathRoot(Path.GetFullPath(source)),
            Path.GetPathRoot(Path.GetFullPath(dest)),
            StringComparison.OrdinalIgnoreCase);
        if (sameVolume)
        {
            Directory.Move(source, dest);
            return;
        }

        try
        {
            CopyDirectory(source, dest);
            VerifyCopy(source, dest);
        }
        catch
        {
            try { if (Directory.Exists(dest)) Directory.Delete(dest, recursive: true); } catch { /* 清不掉就留著，來源仍完整 */ }
            throw;
        }

        Directory.Delete(source, recursive: true);
    }

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)));
        foreach (var dir in Directory.GetDirectories(source))
            CopyDirectory(dir, Path.Combine(dest, Path.GetFileName(dir)));
    }

    private static void VerifyCopy(string source, string dest)
    {
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var copied = Path.Combine(dest, Path.GetRelativePath(source, file));
            if (!File.Exists(copied) || new FileInfo(copied).Length != new FileInfo(file).Length)
                throw new IOException($"複製後核對不符：{Path.GetRelativePath(source, file)}");
        }
    }
}
