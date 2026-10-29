using System.Globalization;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;
using SslWebConsole.Models;

namespace SslWebConsole.Services;

/// <summary>讀進來的品牌 CSV：內容、讀取當下的修改時間（存檔前比對用）、原本的編碼（寫回時沿用）。</summary>
public sealed record BrandCsvSnapshot(string Brand, List<DomainRecord> Rows, DateTime LastWriteUtc, Encoding Encoding);

/// <summary>品牌 CSV 在讀進來之後被別的程式（通常是 Excel）改過，不能直接覆蓋。</summary>
public sealed class BrandCsvChangedException(string message) : Exception(message);

/// <summary>
/// 讀寫人工模式的品牌 CSV。欄位格式跟 SSL_List.csv 一樣（DomainRecord），FtpPass 是明碼（使用者的決定）。
/// - 編碼：有 BOM 的 UTF-8、沒 BOM 的 UTF-8、Big5（Excel「另存 CSV」的預設）都能讀，寫回時維持原本的編碼。
/// - 覆蓋保護：存檔前比對檔案修改時間，讀進來之後被 Excel 改過就拒絕，不會把 Excel 的修改蓋掉；
///   檔案被 Excel 開著（鎖住）時回傳看得懂的訊息。
/// - 先寫到暫存檔再換上去，寫到一半出錯不會留下壞掉的 CSV。
/// </summary>
public sealed class BrandCsvStore
{
    private readonly ManualWorkspace _workspace;

    public BrandCsvStore(ManualWorkspace workspace) => _workspace = workspace;

    public BrandCsvSnapshot Load(string brand)
    {
        var path = _workspace.BrandCsvPath(brand);
        var bytes = File.ReadAllBytes(path);
        var encoding = DetectEncoding(bytes);
        var rows = Parse(encoding.GetString(SkipBom(bytes, encoding)));
        return new BrandCsvSnapshot(brand, rows, File.GetLastWriteTimeUtc(path), encoding);
    }

    /// <summary>
    /// 在網頁上直接建立一個新品牌：BrandCsv 底下產生一個只有標題列的 CSV（UTF-8 帶 BOM，
    /// 跟 SSL_List.csv 一樣，Excel 打開中文不會亂碼）。名稱不合法或已經存在時回傳原因，成功回 null。
    /// </summary>
    public string? Create(string brand)
    {
        brand = brand.Trim();
        if (brand.Length == 0) return "請輸入品牌名稱。";
        if (brand.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || brand.Contains('.'))
            return "品牌名稱不能有 \\ / : * ? \" < > | 或句點。";
        if (brand.EndsWith(ManualWorkspace.CompletedSuffix, StringComparison.OrdinalIgnoreCase))
            return $"名稱不能以「{ManualWorkspace.CompletedSuffix}」結尾（那是封存紀錄檔用的）。";

        var path = _workspace.BrandCsvPath(brand);
        if (File.Exists(path)) return $"{brand}.csv 已經存在。";

        Directory.CreateDirectory(_workspace.BrandCsvDir);
        using var writer = new StreamWriter(path, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        using var csv = new CsvWriter(writer, new CsvConfiguration(CultureInfo.InvariantCulture));
        csv.WriteHeader<DomainRecord>();
        csv.NextRecord();
        return null;
    }

    /// <summary>覆蓋寫回品牌 CSV。回傳新的快照（修改時間更新了，下一次存檔要用它比對）。</summary>
    public BrandCsvSnapshot Save(BrandCsvSnapshot loaded, IEnumerable<DomainRecord> rows)
    {
        var path = _workspace.BrandCsvPath(loaded.Brand);
        if (File.Exists(path) && File.GetLastWriteTimeUtc(path) != loaded.LastWriteUtc)
            throw new BrandCsvChangedException($"{Path.GetFileName(path)} 在讀進來之後被改過（可能是在 Excel 裡存檔），為了不蓋掉那些修改，這次沒有存檔。請按「重新載入」再改一次。");

        var list = rows.Where(r => !string.IsNullOrWhiteSpace(r.CommonName)).ToList();
        WriteAtomically(path, list, loaded.Encoding);
        return loaded with { Rows = list, LastWriteUtc = File.GetLastWriteTimeUtc(path) };
    }

    /// <summary>
    /// 封存完成後呼叫：把這幾個網域從品牌 CSV 搬到「&lt;品牌&gt;_完成.csv」。
    /// 順序是先附加到 _完成.csv、確認寫入成功才從原檔移除（移除前備份成 .csv.bak），
    /// 中途出錯最多是兩邊各有一份，不會整筆消失。
    /// </summary>
    public int MoveToCompleted(string brand, IReadOnlyCollection<string> commonNames)
    {
        if (commonNames.Count == 0) return 0;
        var names = commonNames.Select(n => n.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var current = Load(brand);
        var done = current.Rows.Where(r => names.Contains(r.CommonName.Trim())).ToList();
        if (done.Count == 0) return 0;

        var completedPath = Path.Combine(_workspace.BrandCsvDir, $"{brand}{ManualWorkspace.CompletedSuffix}.csv");
        var completedRows = new List<DomainRecord>();
        var completedEncoding = current.Encoding;
        if (File.Exists(completedPath))
        {
            var bytes = File.ReadAllBytes(completedPath);
            completedEncoding = DetectEncoding(bytes);
            completedRows = Parse(completedEncoding.GetString(SkipBom(bytes, completedEncoding)));
        }
        completedRows.AddRange(done);
        WriteAtomically(completedPath, completedRows, completedEncoding);

        var path = _workspace.BrandCsvPath(brand);
        File.Copy(path, path + ".bak", overwrite: true);
        Save(current, current.Rows.Where(r => !names.Contains(r.CommonName.Trim())));
        return done.Count;
    }

    /// <summary>其他品牌 CSV 裡也有的網域：網域名稱 → 那個品牌。兩個品牌會共用同一個網域資料夾，
    /// 互相蓋掉私鑰和憑證，存檔前要擋下。讀不了的品牌 CSV（例如被 Excel 鎖住）就略過。</summary>
    public Dictionary<string, string> FindInOtherBrands(string brand, IEnumerable<string> commonNames)
    {
        var wanted = commonNames.Select(n => n.Trim()).Where(n => n.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var other in _workspace.ListBrands().Where(b => !string.Equals(b, brand, StringComparison.OrdinalIgnoreCase)))
        {
            List<DomainRecord> rows;
            try { rows = Load(other).Rows; }
            catch (IOException) { continue; }
            foreach (var r in rows)
            {
                var name = r.CommonName.Trim();
                if (wanted.Contains(name)) found.TryAdd(name, other);
            }
        }
        return found;
    }

    /// <summary>檔案被別的程式（通常是 Excel）開著鎖住。</summary>
    public static bool IsLocked(IOException ex) =>
        ex is not FileNotFoundException and not DirectoryNotFoundException;

    private static List<DomainRecord> Parse(string text)
    {
        using var reader = new StringReader(text);
        using var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            MissingFieldFound = null,
            HeaderValidated = null,
        });
        return csv.GetRecords<DomainRecord>()
            .Where(r => !string.IsNullOrWhiteSpace(r.CommonName))
            .ToList();
    }

    private static void WriteAtomically(string path, List<DomainRecord> rows, Encoding encoding)
    {
        // 檔案被 Excel 開著時，直接換檔 Windows 回的是「存取被拒」，跟真的沒權限分不出來；
        // 先試著獨佔開一次，被鎖住會丟 IOException（IsLocked 判斷得出來），頁面就能提示「請先關閉 Excel」。
        if (File.Exists(path))
        {
            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        }

        var temp = path + ".tmp";
        using (var writer = new StreamWriter(temp, false, encoding))
        using (var csv = new CsvWriter(writer, new CsvConfiguration(CultureInfo.InvariantCulture)))
        {
            csv.WriteRecords(rows);
        }
        try
        {
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); } catch { /* 暫存檔刪不掉就留著，原檔沒被動到 */ }
            throw;
        }
    }

    /// <summary>有 UTF-8 BOM → UTF-8（寫回也帶 BOM）；沒有 BOM 但整份是合法 UTF-8 → UTF-8 不帶 BOM；
    /// 否則當成 Big5（Excel 在繁中 Windows 另存 CSV 的預設）。</summary>
    private static Encoding DetectEncoding(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        try
        {
            new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding(950);
        }
    }

    private static byte[] SkipBom(byte[] bytes, Encoding encoding) =>
        encoding is UTF8Encoding && bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF
            ? bytes[3..]
            : bytes;
}
