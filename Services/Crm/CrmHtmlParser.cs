using System.Text.RegularExpressions;
using SslWebConsole.Models;

namespace SslWebConsole.Services;

/// <summary>
/// crm_common.ps1 的 C# 對應：純函式（吃 HTML 字串、吐物件），不碰網路。
/// 對應「藝誠內部案件管控系統」(work.eztrust.tw) 詳細頁的 ASP.NET WebForms 標記。
/// </summary>
public static partial class CrmHtmlParser
{
    // 詳細頁欄位對應（WebForms 的 input id）
    private static readonly Dictionary<string, string> FieldMap = new()
    {
        ["IP"] = "ctl00_ContentPlaceHolder1_isp_ip",
        ["FtpUser"] = "ctl00_ContentPlaceHolder1_isp_ftp_id",
        ["FtpPass"] = "ctl00_ContentPlaceHolder1_isp_ftp_pws",
        ["Address"] = "ctl00_ContentPlaceHolder1_office_address",
    };

    // 中文縣市 -> 英文（只取地址開頭兩字，跟原腳本一致；新竹/嘉義不分市縣，因為地址前兩字分不出來）
    private static readonly Dictionary<string, string> LocalityMap = new()
    {
        ["台北"] = "Taipei", ["臺北"] = "Taipei",
        ["新北"] = "New Taipei",
        ["桃園"] = "Taoyuan",
        ["台中"] = "Taichung", ["臺中"] = "Taichung",
        ["台南"] = "Tainan", ["臺南"] = "Tainan",
        ["高雄"] = "Kaohsiung",
        ["基隆"] = "Keelung",
        ["新竹"] = "Hsinchu",
        ["苗栗"] = "Miaoli",
        ["彰化"] = "Changhua",
        ["南投"] = "Nantou",
        ["雲林"] = "Yunlin",
        ["嘉義"] = "Chiayi",
        ["屏東"] = "Pingtung",
        ["宜蘭"] = "Yilan",
        ["花蓮"] = "Hualien",
        ["台東"] = "Taitung", ["臺東"] = "Taitung",
        ["澎湖"] = "Penghu",
        ["金門"] = "Kinmen",
        ["連江"] = "Lienchiang",
    };

    // id 是執行期才知道的字串，GeneratedRegex 只能吃編譯期常數樣式，這裡改用一般 Regex 動態組。
    private static string EscapeId(string id) => Regex.Escape(id);

    public static string? GetInputValueById(string html, string id)
    {
        var idEsc = EscapeId(id);

        var m = Regex.Match(html, $@"<input[^>]*\bvalue=""([^""]*)""[^>]*\bid=""{idEsc}""", RegexOptions.IgnoreCase);
        if (m.Success) return ResolveHtmlEntity(m.Groups[1].Value);

        m = Regex.Match(html, $@"<input[^>]*\bid=""{idEsc}""[^>]*\bvalue=""([^""]*)""", RegexOptions.IgnoreCase);
        if (m.Success) return ResolveHtmlEntity(m.Groups[1].Value);

        if (Regex.IsMatch(html, $@"<input[^>]*\bid=""{idEsc}""", RegexOptions.IgnoreCase)) return "";

        return null;
    }

    public static string ResolveHtmlEntity(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value ?? "";
        var v = value;
        v = v.Replace("&quot;", "\"");
        v = v.Replace("&#39;", "'");
        v = v.Replace("&apos;", "'");
        v = v.Replace("&lt;", "<");
        v = v.Replace("&gt;", ">");
        v = v.Replace("&nbsp;", " ");
        v = v.Replace("&amp;", "&"); // 必須最後處理，否則會二次解碼
        return v;
    }

    // 業務登打地址開頭的郵遞區號格式很亂：黏著（「412台中市」）、5碼（「41203台中市」）、
    // 中間有空白（「407 台中市」）、全形空白/全形數字（「４０７　台中市」）、括號（「(407)台中市」）。
    // 比對縣市前把開頭所有「不是中文字」的字元整段去掉，不管郵遞區號長怎樣都處理得到。
    [GeneratedRegex(@"^[^\p{IsCJKUnifiedIdeographs}]+")]
    private static partial Regex LeadingNonChineseRegex();

    // 地址前面偶爾會多寫國名（「台灣台中市…」「中華民國臺中市…」），去掉後才是縣市。
    [GeneratedRegex(@"^(中華民國|台灣|臺灣)[^\p{IsCJKUnifiedIdeographs}]*")]
    private static partial Regex LeadingCountryRegex();

    public static (string City, string Locality) ConvertToLocality(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return ("", "");
        var addr = LeadingNonChineseRegex().Replace(address, "");
        addr = LeadingCountryRegex().Replace(addr, "");
        addr = LeadingNonChineseRegex().Replace(addr, "");
        if (addr.Length < 2) return ("", "");
        var city = addr[..2];
        return LocalityMap.TryGetValue(city, out var locality) ? (city, locality) : (city, "");
    }

    public static CrmDetail ParseDetail(string html)
    {
        var ip = GetInputValueById(html, FieldMap["IP"]);
        var ftpUser = GetInputValueById(html, FieldMap["FtpUser"]);
        var ftpPass = GetInputValueById(html, FieldMap["FtpPass"]);
        var address = GetInputValueById(html, FieldMap["Address"]);

        var parsed = !(ip is null && ftpUser is null && ftpPass is null && address is null);
        var (city, locality) = ConvertToLocality(address);

        return new CrmDetail(
            (ip ?? "").Trim(), (ftpUser ?? "").Trim(), (ftpPass ?? "").Trim(), (address ?? "").Trim(),
            city, locality, parsed);
    }

    /// <summary>收集搜尋頁所有 hidden/text/password input 與 select 已選值，原封不動回傳給下一次 postback 用。</summary>
    public static Dictionary<string, string> GetFormFields(string html)
    {
        var fields = new Dictionary<string, string>();

        foreach (Match m in Regex.Matches(html, @"<input[^>]*>", RegexOptions.IgnoreCase))
        {
            var tag = m.Value;
            var typeMatch = Regex.Match(tag, @"\btype=""([^""]*)""", RegexOptions.IgnoreCase);
            var type = typeMatch.Success ? typeMatch.Groups[1].Value.ToLowerInvariant() : "text";
            if (type is not ("hidden" or "text" or "password")) continue;

            var nameMatch = Regex.Match(tag, @"\bname=""([^""]*)""", RegexOptions.IgnoreCase);
            if (!nameMatch.Success) continue;

            var valueMatch = Regex.Match(tag, @"\bvalue=""([^""]*)""", RegexOptions.IgnoreCase);
            fields[nameMatch.Groups[1].Value] = valueMatch.Success ? ResolveHtmlEntity(valueMatch.Groups[1].Value) : "";
        }

        foreach (Match m in Regex.Matches(html, @"<select[^>]*\bname=""([^""]*)""[^>]*>(.*?)</select>", RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            var name = m.Groups[1].Value;
            var body = m.Groups[2].Value;

            var sel = Regex.Match(body, @"<option[^>]*\bselected[^>]*\bvalue=""([^""]*)""", RegexOptions.IgnoreCase);
            if (!sel.Success) sel = Regex.Match(body, @"<option[^>]*\bvalue=""([^""]*)""[^>]*\bselected", RegexOptions.IgnoreCase);
            if (!sel.Success) sel = Regex.Match(body, @"<option[^>]*\bvalue=""([^""]*)""", RegexOptions.IgnoreCase);
            if (sel.Success) fields[name] = sel.Groups[1].Value;
        }

        return fields;
    }

    public static List<string> GetRowCells(string rowHtml)
    {
        var cells = new List<string>();
        foreach (Match m in Regex.Matches(rowHtml, @"<td[^>]*>(.*?)</td>", RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            var txt = ResolveHtmlEntity(Regex.Replace(m.Groups[1].Value, "<[^>]*>", " "));
            txt = Regex.Replace(txt, @"\s+", " ").Trim();
            if (txt.Length > 0) cells.Add(txt);
        }
        return cells;
    }

    public static bool IsLoginPage(string? html)
    {
        if (string.IsNullOrEmpty(html)) return false;
        if (Regex.IsMatch(html, "尚未登錄系統|請重新登錄|登錄時間逾時")) return true;
        if (Regex.IsMatch(html, @"document\.location\s*=\s*'\.\./index\.aspx'")) return true;
        if (Regex.IsMatch(html, "reCAPTCHA|g-recaptcha|我不是機器人", RegexOptions.IgnoreCase)) return true;
        return false;
    }
}
