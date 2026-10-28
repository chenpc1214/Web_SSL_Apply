using System.Globalization;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;
using SslWebConsole.Models;

namespace SslWebConsole.Services;

/// <summary>讀寫 SSL_List.csv，與 .ps1 腳本讀的是同一份檔案。</summary>
public sealed class CsvDomainStore
{
    private readonly NicSrsConfigLoader _configLoader;

    public CsvDomainStore(NicSrsConfigLoader configLoader)
    {
        _configLoader = configLoader;
    }

    public string CsvPath => Path.Combine(_configLoader.ResolveOutputRoot(), "SSL_List.csv");

    public List<DomainRecord> Load()
    {
        if (!File.Exists(CsvPath))
            return [];

        using var reader = new StreamReader(CsvPath, detectEncodingFromByteOrderMarks: true);
        using var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            MissingFieldFound = null,
            HeaderValidated = null,
        });
        return csv.GetRecords<DomainRecord>()
            .Where(r => !string.IsNullOrWhiteSpace(r.CommonName))
            .ToList();
    }

    public void Save(IEnumerable<DomainRecord> records)
    {
        // UTF8 with BOM，跟現有 .ps1 用 -Encoding UTF8 寫出的格式一致，PS 5.1 讀中文才不會亂碼。
        using var writer = new StreamWriter(CsvPath, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        using var csv = new CsvWriter(writer, new CsvConfiguration(CultureInfo.InvariantCulture));
        csv.WriteRecords(records);
    }
}
