using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using SslWebConsole.Models;

namespace SslWebConsole.Services;

/// <summary>
/// 步驟1（對應 1_Check_DNS.ps1）：逐列查 A 記錄（含 www 變體）比對 CSV IP。
/// 分類 OK / OK(www異常) / OK-www / FAIL / 略過；單列失敗不中斷整批。
/// </summary>
public sealed partial class DnsCheckService
{
    [GeneratedRegex(@"[\d\.]+")]
    private static partial Regex IpLikeRegex();

    [GeneratedRegex(@"^\*\.")]
    private static partial Regex WildcardDomainRegex();

    [GeneratedRegex(@"^www\.", RegexOptions.IgnoreCase)]
    private static partial Regex WwwPrefixRegex();

    private static async Task<List<string>> ResolveDomainIpAsync(string domain)
    {
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(domain);
            return addresses
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => a.ToString())
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    public async Task<List<DnsCheckResult>> CheckAsync(IEnumerable<DomainRecord> rows, string? targetDomainFilter = null)
    {
        var results = new List<DnsCheckResult>();

        foreach (var row in rows)
        {
            var domain = row.CommonName?.Trim() ?? "";

            if (!string.IsNullOrWhiteSpace(targetDomainFilter) && !Regex.IsMatch(domain, targetDomainFilter))
                continue;

            var csvIp = row.Ip?.Trim() ?? "";

            // SKIP：IP 欄位空白或格式不像 IP（沿用原腳本用 "IP" 字樣偵測誤填表頭的防呆）
            if (string.IsNullOrWhiteSpace(csvIp) || csvIp.Contains("IP", StringComparison.OrdinalIgnoreCase) || !IpLikeRegex().IsMatch(csvIp))
                continue;

            // SKIP：萬用字元網域（DNS 檢查對 *.example.com 沒意義）
            if (WildcardDomainRegex().IsMatch(domain))
                continue;

            var mainIps = await ResolveDomainIpAsync(domain);
            var mainIpDisplay = mainIps.Count > 0 ? string.Join(", ", mainIps) : "(無法解析)";

            var alreadyWww = WwwPrefixRegex().IsMatch(domain);
            var wwwDomain = alreadyWww ? domain : $"www.{domain}";
            var wwwIps = await ResolveDomainIpAsync(wwwDomain);
            var wwwIpDisplay = wwwIps.Count > 0 ? string.Join(", ", wwwIps) : "(無法解析)";

            var mainMatch = mainIps.Contains(csvIp);
            var wwwMatch = wwwIps.Contains(csvIp);

            DnsStatus status;
            string remark, effectiveName = domain;

            if (mainMatch && wwwMatch)
            {
                status = DnsStatus.Ok;
                remark = "";
            }
            else if (mainMatch && !wwwMatch)
            {
                if (alreadyWww)
                {
                    status = DnsStatus.Ok;
                    remark = "";
                }
                else
                {
                    status = DnsStatus.OkWwwWarn;
                    remark = $"期望: {csvIp}";
                }
            }
            else if (!mainMatch && wwwMatch)
            {
                status = DnsStatus.OkWww;
                remark = $"期望: {csvIp}";
                effectiveName = wwwDomain; // OK-www：網域欄改為 www.CommonName
            }
            else
            {
                status = DnsStatus.Fail;
                remark = $"期望: {csvIp}";
            }

            results.Add(new DnsCheckResult
            {
                Row = row,
                Status = status,
                EffectiveCommonName = effectiveName,
                MainIpDisplay = mainIpDisplay,
                WwwIpDisplay = wwwIpDisplay,
                MainMatch = mainMatch,
                WwwMatch = wwwMatch,
                Remark = remark,
            });
        }

        return results;
    }
}
