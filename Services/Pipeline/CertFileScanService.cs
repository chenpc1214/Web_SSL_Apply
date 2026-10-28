using System.Globalization;
using CsvHelper;
using CsvHelper.Configuration;
using SslWebConsole.Models;

namespace SslWebConsole.Services;

/// <summary>
/// 步驟6a（對應 7_Find_Cert_Files.ps1）：遞迴掃描 .crt/.key/.ca-bundle，依「父資料夾名稱」
/// （底線還原成點）分組成網域；只有 .crt + .key 都存在才納入清單，輸出 Certificates_List.csv。
/// 輸出位置在 WorkRoot 的上一層（WorkRootResolver.ResolveOutputRoot）——跟各網域工作
/// 資料夾分開放，不是固定在 ScriptsRoot。PFX_Storage 也是同樣邏輯（見 Certificates.razor）。
/// </summary>
public sealed class CertFileScanService
{
    private readonly WorkRootResolver _workRoot;

    public CertFileScanService(WorkRootResolver workRoot)
    {
        _workRoot = workRoot;
    }

    public string OutputCsvPath => Path.Combine(_workRoot.ResolveOutputRoot(), "Certificates_List.csv");

    public List<CertFileGroup> Scan(string? rootPathOverride = null)
    {
        var rootPath = string.IsNullOrWhiteSpace(rootPathOverride) ? _workRoot.Resolve() : Path.GetFullPath(rootPathOverride);
        if (!Directory.Exists(rootPath))
            return [];

        var groups = new Dictionary<string, (string? Cert, string? Key, string? Bundle)>();

        foreach (var file in Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories))
        {
            var ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext is not (".crt" or ".key" or ".ca-bundle")) continue;

            var directoryName = Path.GetFileName(Path.GetDirectoryName(file)) ?? "";
            var domainName = directoryName.Replace('_', '.');

            groups.TryGetValue(domainName, out var g);
            g = ext switch
            {
                ".crt" => (file, g.Key, g.Bundle),
                ".key" => (g.Cert, file, g.Bundle),
                ".ca-bundle" => (g.Cert, g.Key, file),
                _ => g,
            };
            groups[domainName] = g;
        }

        var result = groups
            .Where(kv => kv.Value.Cert is not null && kv.Value.Key is not null)
            .Select(kv => new CertFileGroup
            {
                Domain = kv.Key,
                CertPath = kv.Value.Cert!,
                KeyPath = kv.Value.Key!,
                BundlePath = kv.Value.Bundle,
            })
            .OrderBy(g => g.Domain, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return result;
    }

    public void SaveCsv(IEnumerable<CertFileGroup> groups)
    {
        var list = groups.ToList();
        using (var writer = new StreamWriter(OutputCsvPath, false, new System.Text.UTF8Encoding(true)))
        using (var csv = new CsvWriter(writer, new CsvConfiguration(CultureInfo.InvariantCulture)))
        {
            csv.WriteRecords(list);
        }
    }

    public List<CertFileGroup> LoadCsv()
    {
        if (!File.Exists(OutputCsvPath)) return [];
        using var reader = new StreamReader(OutputCsvPath, detectEncodingFromByteOrderMarks: true);
        using var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            MissingFieldFound = null,
            HeaderValidated = null,
        });
        return csv.GetRecords<CertFileGroup>().ToList();
    }
}
