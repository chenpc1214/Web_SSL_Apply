namespace SslWebConsole.Services;

public sealed record MigrationResult(int Moved, int Skipped, List<string> Log);

/// <summary>
/// 換 WorkRoot 時，幫忙把舊路徑底下「看起來像網域資料夾」的東西搬到新路徑。
/// 判定邏輯比照 指定路徑生網域資料夾說明.md 第四節的手動搬移指令碼：
/// 資料夾內含 .key 或 .csr 檔案才算數，避免誤搬 PFX_Storage 等其他資料夾。
/// </summary>
public sealed class WorkRootMigrationService
{
    public static List<string> FindDomainFolders(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return [];

        return Directory.GetDirectories(root)
            .Where(dir => Directory.EnumerateFiles(dir, "*.key").Any() || Directory.EnumerateFiles(dir, "*.csr").Any())
            .ToList();
    }

    public static MigrationResult Migrate(string toRoot, IEnumerable<string> folders)
    {
        Directory.CreateDirectory(toRoot);
        var log = new List<string>();
        int moved = 0, skipped = 0;

        foreach (var folder in folders)
        {
            var name = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var dest = Path.Combine(toRoot, name);
            if (Directory.Exists(dest))
            {
                skipped++;
                log.Add($"略過 {name}：目的地已有同名資料夾");
                continue;
            }
            Directory.Move(folder, dest);
            moved++;
            log.Add($"已搬移 {name}");
        }

        return new MigrationResult(moved, skipped, log);
    }
}
