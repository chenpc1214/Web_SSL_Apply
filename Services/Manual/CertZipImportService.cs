using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace SslWebConsole.Services;

public enum ZipImportStatus
{
    /// <summary>可以匯入：找到網域資料夾、私鑰對得上。</summary>
    Ready,
    /// <summary>資料夾裡已經是同一張憑證，不用重寫；ZIP 一樣會刪掉。</summary>
    AlreadyImported,
    /// <summary>同一個網域還有簽發日期更新的 ZIP，這個不用；ZIP 留著讓使用者自己刪。</summary>
    SkippedOlder,
    /// <summary>不能匯入（找不到主憑證/資料夾/私鑰、私鑰對不上…），ZIP 留著方便檢查。</summary>
    Failed,
}

/// <summary>預覽時每個 ZIP 一筆。LeafPem/BundlePem 是預覽當下讀出來的內容，匯入時直接寫這份，不再重新解析。</summary>
public sealed record ZipImportItem(
    string ZipPath,
    ZipImportStatus Status,
    string Message,
    string? CommonName = null,
    string? FolderName = null,
    DateTime? NotBefore = null,
    DateTime? NotAfter = null,
    string? LeafPem = null,
    string? BundlePem = null,
    bool HasPfx = false)
{
    public string ZipName => Path.GetFileName(ZipPath);
}

public sealed record ZipImportResult(int Imported, int AlreadyImported, int ZipsDeleted, List<string> Log);

/// <summary>
/// 人工模式「匯入憑證 ZIP」，改寫自 _SSL_Renew\6_Import_Cert_Zip.ps1：
///   1. 讀 ZipCrt 底下每個 .zip（直接讀壓縮檔內容，不解壓到硬碟）
///   2. 主憑證（leaf）＝ Basic Constraints 不是 CA 的那張；它的 CN 換成資料夾名稱（. 與 * 換成 _）
///   3. 寫出 &lt;資料夾&gt;.crt（只有主憑證）和 &lt;資料夾&gt;.ca-bundle（中繼/根憑證，依鏈序 leaf→root；沒有就不寫）
/// 比 .ps1 多的保護：
///   - 先預覽、使用者確認才寫檔
///   - 寫入前比對憑證和資料夾裡 .key 的公鑰，對不上就不寫（避免放錯 ZIP，打包 PFX 時才發現）
///   - CN 找不到資料夾時，用 SAN 裡的網域名稱再找一次
///   - 同一個網域有好幾個 ZIP 時只用簽發日期最新的
///   - 一個檔案裡有多張憑證（fullchain/bundle）也讀得到，.ps1 用 openssl x509 只會讀到第一張
/// 匯入成功（或資料夾裡已經是同一張）的 ZIP 會刪掉；失敗、較舊的留著。
/// </summary>
public sealed class CertZipImportService
{
    private static readonly string[] CertExtensions = [".crt", ".cer", ".pem", ".ca-bundle", ".cabundle"];
    private const long MaxEntryBytes = 1024 * 1024; // 憑證檔不可能超過 1MB，太大的直接略過

    private readonly ManualWorkspace _workspace;

    public CertZipImportService(ManualWorkspace workspace) => _workspace = workspace;

    /// <summary>只看不動：列出每個 ZIP 會怎麼處理。</summary>
    public List<ZipImportItem> Preview()
    {
        var zipDir = _workspace.ZipDir;
        if (!Directory.Exists(zipDir)) return [];

        var items = Directory.GetFiles(zipDir, "*.zip")
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .Select(f => Inspect(f))
            .ToList();

        // 同一個網域有好幾個可匯入的 ZIP：只留簽發日期（NotBefore）最新的，其他標成較舊
        foreach (var group in items.Where(i => (i.Status is ZipImportStatus.Ready or ZipImportStatus.AlreadyImported) && i.FolderName is not null)
                                   .GroupBy(i => i.FolderName!, StringComparer.OrdinalIgnoreCase)
                                   .Where(g => g.Count() > 1)
                                   .ToList())
        {
            var newest = group.OrderByDescending(i => i.NotBefore).First();
            foreach (var older in group.Where(i => !ReferenceEquals(i, newest)))
            {
                var index = items.IndexOf(older);
                items[index] = older with
                {
                    Status = ZipImportStatus.SkippedOlder,
                    Message = $"略過（較舊）：{newest.ZipName} 是同一個網域、簽發日期比較新的憑證",
                };
            }
        }
        return items;
    }

    /// <summary>照預覽結果寫檔、刪 ZIP。寫檔前再確認一次資料夾還在，ZIP 刪不掉就留著並記錄。</summary>
    public ZipImportResult Import(IEnumerable<ZipImportItem> preview)
    {
        var log = new List<string>();
        int imported = 0, already = 0, deleted = 0;

        foreach (var item in preview)
        {
            if (item.Status == ZipImportStatus.AlreadyImported)
            {
                already++;
                log.Add($"{item.ZipName}：{item.CommonName} 已經是同一張憑證，沒有重寫");
                deleted += TryDeleteZip(item, log);
                continue;
            }
            if (item.Status != ZipImportStatus.Ready) continue;

            var dir = Path.Combine(_workspace.DomainRoot, item.FolderName!);
            if (!Directory.Exists(dir))
            {
                log.Add($"{item.ZipName}：網域資料夾 {item.FolderName} 不見了，沒有匯入");
                continue;
            }

            try
            {
                var crtPath = Path.Combine(dir, $"{item.FolderName}.crt");
                var bundlePath = Path.Combine(dir, $"{item.FolderName}.ca-bundle");
                WritePem(crtPath, item.LeafPem!);
                if (item.BundlePem is not null)
                {
                    WritePem(bundlePath, item.BundlePem);
                }
                else if (File.Exists(bundlePath))
                {
                    // 新憑證沒有中繼憑證，舊的 .ca-bundle 留著會在打包 PFX 時被包進去，鏈就錯了
                    File.Delete(bundlePath);
                    log.Add($"{item.ZipName}：新憑證沒有中繼憑證，已刪除舊的 {item.FolderName}.ca-bundle");
                }
                imported++;
                log.Add($"{item.ZipName}：已匯入 {item.CommonName}（到期 {item.NotAfter:yyyy-MM-dd}）"
                    + (item.HasPfx ? "。資料夾裡已經有 PFX，記得重新打包" : ""));
            }
            catch (Exception ex)
            {
                log.Add($"{item.ZipName}：寫入 {item.FolderName} 失敗：{ex.Message}（ZIP 保留）");
                continue;
            }

            deleted += TryDeleteZip(item, log);
        }

        return new ZipImportResult(imported, already, deleted, log);
    }

    private ZipImportItem Inspect(string zipPath)
    {
        List<X509Certificate2> certs;
        try { certs = ReadCertificates(zipPath); }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            return new ZipImportItem(zipPath, ZipImportStatus.Failed, $"讀不了這個 ZIP：{ex.Message}");
        }

        if (certs.Count == 0)
            return new ZipImportItem(zipPath, ZipImportStatus.Failed, "ZIP 裡找不到任何憑證");

        var leaves = certs.Where(c => !IsCa(c)).ToList();
        if (leaves.Count == 0)
            return new ZipImportItem(zipPath, ZipImportStatus.Failed, "找不到主憑證（只有中繼/根憑證）");
        if (leaves.Count > 1)
            return new ZipImportItem(zipPath, ZipImportStatus.Failed,
                $"ZIP 裡有 {leaves.Count} 張主憑證（{string.Join("、", leaves.Select(c => CommonNameOf(c)))}），請手動處理");

        var leaf = leaves[0];
        var cn = CommonNameOf(leaf);
        var notBefore = leaf.NotBefore;
        var notAfter = leaf.NotAfter;

        var folderName = FindDomainFolder(leaf);
        if (folderName is null)
            return new ZipImportItem(zipPath, ZipImportStatus.Failed,
                $"找不到對應的網域資料夾（CN={cn}），請先在步驟2產生 CSR", cn, NotBefore: notBefore, NotAfter: notAfter);

        var dir = Path.Combine(_workspace.DomainRoot, folderName);
        var keyPath = Path.Combine(dir, $"{folderName}.key");
        if (!File.Exists(keyPath))
            return new ZipImportItem(zipPath, ZipImportStatus.Failed,
                $"資料夾 {folderName} 裡沒有私鑰 {folderName}.key，沒辦法確認憑證對不對", cn, folderName, notBefore, notAfter);

        var keyCheck = MatchesKey(leaf, keyPath);
        if (keyCheck is not null)
            return new ZipImportItem(zipPath, ZipImportStatus.Failed, keyCheck, cn, folderName, notBefore, notAfter);

        var leafPem = leaf.ExportCertificatePem();
        var cas = certs.Where(c => IsCa(c))
            .GroupBy(c => c.Thumbprint).Select(g => g.First()) // 同一張中繼憑證可能在好幾個檔案裡都有
            .ToList();
        var bundlePem = cas.Count > 0
            ? string.Join("\n", SortChain(leaf, cas).Select(c => c.ExportCertificatePem()))
            : null;

        var hasPfx = Directory.EnumerateFiles(dir, "*.pfx").Any();
        var crtPath = Path.Combine(dir, $"{folderName}.crt");
        if (File.Exists(crtPath) && Normalize(File.ReadAllText(crtPath)) == Normalize(leafPem))
            return new ZipImportItem(zipPath, ZipImportStatus.AlreadyImported, "資料夾裡已經是同一張憑證",
                cn, folderName, notBefore, notAfter, leafPem, bundlePem, hasPfx);

        // 資料夾裡已經是比較新的憑證（例如較新的 ZIP 已經匯入並刪掉了，舊的還留在 ZipCrt）：不能拿舊的蓋回去
        if (ExistingNotBefore(crtPath) is { } existingStart && existingStart > notBefore)
            return new ZipImportItem(zipPath, ZipImportStatus.SkippedOlder,
                $"略過（較舊）：資料夾裡已經是 {existingStart:yyyy-MM-dd} 簽發、比較新的憑證", cn, folderName, notBefore, notAfter);

        var note = File.Exists(crtPath) ? "會取代資料夾裡現有的 .crt" : "可以匯入";
        if (hasPfx) note += "（資料夾裡已經有 PFX，匯入後要重新打包）";
        return new ZipImportItem(zipPath, ZipImportStatus.Ready, note, cn, folderName, notBefore, notAfter, leafPem, bundlePem, hasPfx);
    }

    /// <summary>壓縮檔裡所有像憑證的檔案（依副檔名），每個檔案可能有好幾張（PEM 串接）或是一張 DER。</summary>
    private static List<X509Certificate2> ReadCertificates(string zipPath)
    {
        var result = new List<X509Certificate2>();
        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            if (entry.Length == 0 || entry.Length > MaxEntryBytes) continue;
            if (!CertExtensions.Contains(Path.GetExtension(entry.Name), StringComparer.OrdinalIgnoreCase)) continue;

            byte[] bytes;
            using (var stream = entry.Open())
            using (var ms = new MemoryStream())
            {
                stream.CopyTo(ms);
                bytes = ms.ToArray();
            }

            var text = Encoding.ASCII.GetString(bytes);
            if (text.Contains("-----BEGIN CERTIFICATE-----"))
            {
                var collection = new X509Certificate2Collection();
                try { collection.ImportFromPem(text); }
                catch (CryptographicException) { continue; }
                result.AddRange(collection);
            }
            else
            {
                try { result.Add(X509CertificateLoader.LoadCertificate(bytes)); }
                catch (CryptographicException) { /* 不是憑證（例如說明文字），略過 */ }
            }
        }
        return result;
    }

    private static bool IsCa(X509Certificate2 cert) =>
        cert.Extensions.OfType<X509BasicConstraintsExtension>().FirstOrDefault()?.CertificateAuthority ?? false;

    private static string CommonNameOf(X509Certificate2 cert) => cert.GetNameInfo(X509NameType.SimpleName, false);

    /// <summary>先用 CN 找，找不到再用 SAN 裡的每個網域名稱找；只認已經存在的網域資料夾。</summary>
    private string? FindDomainFolder(X509Certificate2 leaf)
    {
        var candidates = new List<string> { CommonNameOf(leaf) };
        var san = leaf.Extensions.OfType<X509SubjectAlternativeNameExtension>().FirstOrDefault();
        if (san is not null) candidates.AddRange(san.EnumerateDnsNames());

        foreach (var name in candidates.Where(n => !string.IsNullOrWhiteSpace(n)))
        {
            var folder = DomainFolderNaming.ToFolderName(name);
            if (Directory.Exists(Path.Combine(_workspace.DomainRoot, folder))) return folder;
        }
        return null;
    }

    /// <summary>憑證的公鑰跟私鑰檔對不對得上。對得上回 null，否則回原因。</summary>
    private static string? MatchesKey(X509Certificate2 leaf, string keyPath)
    {
        using var certKey = leaf.GetRSAPublicKey();
        if (certKey is null)
            return "憑證不是 RSA 金鑰，跟步驟2產生的私鑰對不上（是不是下單時用了別的 CSR？）";

        using var privateKey = RSA.Create();
        try { privateKey.ImportFromPem(File.ReadAllText(keyPath)); }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException)
        {
            return $"讀不了私鑰 {Path.GetFileName(keyPath)}：{ex.Message}";
        }

        var a = certKey.ExportParameters(false);
        var b = privateKey.ExportParameters(false);
        return a.Modulus.AsSpan().SequenceEqual(b.Modulus) && a.Exponent.AsSpan().SequenceEqual(b.Exponent)
            ? null
            : "憑證和資料夾裡的私鑰對不上（可能放錯 ZIP，或下單時用的不是這份 CSR）";
    }

    /// <summary>從簽發主憑證的那張開始往上排到根；接不上鏈的補在最後，不丟掉（同 .ps1 的 Sort-Chain）。</summary>
    private static List<X509Certificate2> SortChain(X509Certificate2 leaf, List<X509Certificate2> cas)
    {
        var ordered = new List<X509Certificate2>();
        var remaining = new List<X509Certificate2>(cas);
        var wantIssuer = leaf.IssuerName.Name;
        while (remaining.Count > 0)
        {
            var match = remaining.FirstOrDefault(c => c.SubjectName.Name == wantIssuer);
            if (match is null) break;
            ordered.Add(match);
            remaining.Remove(match);
            if (match.SubjectName.Name == match.IssuerName.Name) break; // 自簽的根憑證，到頂了
            wantIssuer = match.IssuerName.Name;
        }
        ordered.AddRange(remaining);
        return ordered;
    }

    /// <summary>資料夾裡現有 .crt 的簽發日期；沒有或讀不了就回 null。</summary>
    private static DateTime? ExistingNotBefore(string crtPath)
    {
        if (!File.Exists(crtPath)) return null;
        try
        {
            using var existing = X509Certificate2.CreateFromPem(File.ReadAllText(crtPath));
            return existing.NotBefore;
        }
        catch (CryptographicException) { return null; }
    }

    private static void WritePem(string path, string pem) =>
        File.WriteAllText(path, pem.TrimEnd('\r', '\n') + "\n", new UTF8Encoding(false));

    private static string Normalize(string pem) => pem.Replace("\r", "").Trim();

    private static int TryDeleteZip(ZipImportItem item, List<string> log)
    {
        try
        {
            if (!File.Exists(item.ZipPath)) return 0;
            File.Delete(item.ZipPath);
            return 1;
        }
        catch (Exception ex)
        {
            log.Add($"{item.ZipName}：已匯入，但 ZIP 刪不掉：{ex.Message}");
            return 0;
        }
    }
}
