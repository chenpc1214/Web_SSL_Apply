using System.Net;
using System.Text.RegularExpressions;
using SslWebConsole.Models;

namespace SslWebConsole.Services;

public sealed class CrmLoginRequiredException : Exception
{
    public CrmLoginRequiredException() : base("Cookie 已失效／被導回登入頁") { }
}

/// <summary>
/// Verify-FromCrm.ps1 的 C# 對應：用「藝誠內部案件管控系統」校對 Domains 的
/// IP / FtpUser / FtpPass / Locality。★ 只查詢、不寫回 CRM，也不會自動改資料庫——
/// 差異只顯示在畫面上，要不要採用由使用者自己判斷、去 /domains 頁手動修改。
/// Cookie 有 reCAPTCHA 擋著沒辦法自動登入，需使用者自行從瀏覽器複製貼上。
/// </summary>
public sealed class CrmVerifyService
{
    private const int DefaultDelayMs = 300;

    public async Task<(bool Ok, string Message)> TestLoginAsync(string cookie, string baseUrl, CancellationToken ct)
    {
        try
        {
            using var client = BuildClient(cookie, baseUrl);
            var searchUrl = $"{baseUrl.TrimEnd('/')}/crm/index.aspx?link_00=crm";
            var html = await client.GetStringAsync(searchUrl, ct);
            return CrmHtmlParser.IsLoginPage(html) ? (false, "Cookie 無效或已過期。") : (true, "Cookie 有效，可以開始校對。");
        }
        catch (Exception ex)
        {
            return (false, $"連線失敗: {ex.Message}");
        }
    }

    /// <summary>
    /// 逐一校對 rows（依 DomainRecord.Id = CRM 案件編號比對），每筆查完立即透過 onResult 回報，
    /// 方便畫面即時更新。targetIdFilter 可只校對案件編號包含此字串的列。
    /// </summary>
    public async Task VerifyAsync(
        IEnumerable<DomainRecord> rows,
        string? targetIdFilter,
        string cookie,
        string baseUrl,
        Action<CrmVerifyResult> onResult,
        CancellationToken ct,
        int delayMs = DefaultDelayMs)
    {
        var targets = rows
            .Where(r => !string.IsNullOrWhiteSpace(r.Id))
            .Where(r => string.IsNullOrWhiteSpace(targetIdFilter) || r.Id.Contains(targetIdFilter, StringComparison.OrdinalIgnoreCase))
            .ToList();

        using var client = BuildClient(cookie, baseUrl);
        var searchUrl = $"{baseUrl.TrimEnd('/')}/crm/index.aspx?link_00=crm";

        string probe;
        try
        {
            probe = await client.GetStringAsync(searchUrl, ct);
        }
        catch (Exception ex)
        {
            onResult(new CrmVerifyResult("", "", CrmVerifyOutcome.Error, [], $"無法連線到 {searchUrl}: {ex.Message}"));
            return;
        }

        if (CrmHtmlParser.IsLoginPage(probe))
        {
            onResult(new CrmVerifyResult("", "", CrmVerifyOutcome.LoginRequired, [], "Cookie 無效或已過期，請重新登入 CRM 並更新 Cookie。"));
            return;
        }

        foreach (var row in targets)
        {
            if (ct.IsCancellationRequested) break;
            var caseNo = row.Id;

            try
            {
                var internalId = await FindInternalIdAsync(client, searchUrl, caseNo, delayMs, ct);
                if (internalId is null)
                {
                    onResult(new CrmVerifyResult(caseNo, row.CommonName, CrmVerifyOutcome.NotFound, [], "CRM 查無此案件編號"));
                    continue;
                }

                var detailUrl = $"{baseUrl.TrimEnd('/')}/customer/add.aspx?id={internalId}&link_00=crm&page=0";
                await Task.Delay(delayMs, ct);
                var html = await client.GetStringAsync(detailUrl, ct);
                if (CrmHtmlParser.IsLoginPage(html)) throw new CrmLoginRequiredException();

                var detail = CrmHtmlParser.ParseDetail(html);
                if (!detail.Parsed)
                {
                    onResult(new CrmVerifyResult(caseNo, row.CommonName, CrmVerifyOutcome.ParseFailed, [], $"詳細頁解析失敗 (id={internalId})", detailUrl));
                    continue;
                }

                var diffs = new List<CrmFieldDiff>();
                if (!string.Equals(row.Ip, detail.Ip, StringComparison.Ordinal))
                    diffs.Add(new CrmFieldDiff("IP", row.Ip, detail.Ip));
                if (!string.Equals(row.FtpUser, detail.FtpUser, StringComparison.Ordinal))
                    diffs.Add(new CrmFieldDiff("FtpUser", row.FtpUser, detail.FtpUser));
                if (!string.Equals(row.FtpPass, detail.FtpPass, StringComparison.Ordinal))
                    diffs.Add(new CrmFieldDiff("FtpPass", row.FtpPass, detail.FtpPass));
                if (!string.Equals(row.Locality ?? "", detail.Locality, StringComparison.OrdinalIgnoreCase))
                    diffs.Add(new CrmFieldDiff("Locality", row.Locality ?? "", detail.Locality));

                string? note = null;
                if (string.IsNullOrWhiteSpace(detail.Locality) && !string.IsNullOrWhiteSpace(detail.Address))
                    note = $"地址「{detail.Address}」的縣市無法對應：{detail.City}";

                onResult(new CrmVerifyResult(
                    caseNo, row.CommonName,
                    diffs.Count == 0 ? CrmVerifyOutcome.Ok : CrmVerifyOutcome.Diff,
                    diffs, note, detailUrl));
            }
            catch (CrmLoginRequiredException)
            {
                onResult(new CrmVerifyResult(caseNo, row.CommonName, CrmVerifyOutcome.LoginRequired, [], "Cookie 已失效，請重新登入並更新 Cookie 後再執行。"));
                break;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                onResult(new CrmVerifyResult(caseNo, row.CommonName, CrmVerifyOutcome.Error, [], ex.Message));
            }
        }
    }

    private static async Task<string?> FindInternalIdAsync(HttpClient client, string searchUrl, string caseNo, int delayMs, CancellationToken ct)
    {
        var keys = new List<string> { caseNo };
        var digitsMatch = Regex.Match(caseNo, @"^(\d+)");
        if (digitsMatch.Success && digitsMatch.Value.Length > 0 && digitsMatch.Value != caseNo)
            keys.Add(digitsMatch.Value);

        foreach (var key in keys)
        {
            ct.ThrowIfCancellationRequested();

            var page = await client.GetStringAsync(searchUrl, ct);
            if (CrmHtmlParser.IsLoginPage(page)) throw new CrmLoginRequiredException();

            var fields = CrmHtmlParser.GetFormFields(page);
            fields["ctl00$ContentPlaceHolder1$search_04"] = key;
            fields["ctl00$ContentPlaceHolder1$ImageButton1.x"] = "10";
            fields["ctl00$ContentPlaceHolder1$ImageButton1.y"] = "10";

            await Task.Delay(delayMs, ct);
            using var content = new FormUrlEncodedContent(fields);
            using var resp = await client.PostAsync(searchUrl, content, ct);
            var res = await resp.Content.ReadAsStringAsync(ct);
            if (CrmHtmlParser.IsLoginPage(res)) throw new CrmLoginRequiredException();

            var flat = Regex.Replace(res, @"\r?\n", " ");
            var candidates = new List<(string Id, List<string> Cells)>();
            foreach (Match rowMatch in Regex.Matches(flat, @"<tr[^>]*>.*?</tr>", RegexOptions.IgnoreCase | RegexOptions.Singleline))
            {
                var rowHtml = rowMatch.Value;
                var idMatch = Regex.Match(rowHtml, @"add\.aspx\?id=(\d+)", RegexOptions.IgnoreCase);
                if (!idMatch.Success) continue;
                candidates.Add((idMatch.Groups[1].Value, CrmHtmlParser.GetRowCells(rowHtml)));
            }
            if (candidates.Count == 0) continue;

            // (1) 精準：某一格正好等於查詢鍵（比照 PowerShell -contains 預設不分大小寫）
            foreach (var c in candidates)
                if (c.Cells.Contains(key, StringComparer.OrdinalIgnoreCase)) return c.Id;

            // (2) 退一步：某格「以查詢鍵開頭、且下一字元非數字」（比照 PowerShell -match 預設不分大小寫）
            var reKey = new Regex("^" + Regex.Escape(key) + @"(?!\d)", RegexOptions.IgnoreCase);
            foreach (var c in candidates)
                foreach (var cell in c.Cells)
                    if (reKey.IsMatch(cell)) return c.Id;
        }

        return null;
    }

    private static HttpClient BuildClient(string cookie, string baseUrl)
    {
        // 原本的 Verify-FromCrm.ps1（Windows PowerShell 5.1 = .NET Framework）用
        // Invoke-WebRequest -WebSession 送 Cookie，對非 ASCII 內容很寬容，會把
        // crm_cookie.txt 裡的原始字串（這個系統的 admin_userInfo 常是「混合編碼」——
        // 姓名是 %e9%99%b3 這種已編碼形式，同一個值裡的中文時間文字卻是未編碼原始字元）
        // 原封不動送出去，親測有效。.NET 6+ 的 HttpClient 預設更嚴格，非 ASCII header
        // 值直接丟例外——但只要嘗試「先解碼再重新編碼」去正規化這個值，送出去的位元組
        // 就會跟 PowerShell 原本送的不一樣，對方（一個行為古怪的 legacy ASP.NET 系統）
        // 就不吃。真正對的做法是完全不要動這個字串，用 RequestHeaderEncodingSelector
        // 讓 SocketsHttpHandler 改用 UTF-8 位元組直接送出 header（放行非 ASCII），
        // 跟 PowerShell 那端的行為一致，不多做任何轉換。
        var handler = new SocketsHttpHandler
        {
            UseCookies = false,
            AllowAutoRedirect = true,
            RequestHeaderEncodingSelector = (_, _) => System.Text.Encoding.UTF8,
        };

        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", cookie.Trim());
        return client;
    }
}
