using System.Text;
using System.Xml;
using SslWebConsole.Models;

namespace SslWebConsole.Services;

/// <summary>
/// 步驟4a（對應 5_Patch_WebConfig.ps1）：在遠端 web.config 注入「放行 .well-known/pki-validation/
/// 不被轉址」的 rewrite rule，避免 http->https / non-www->www 轉址擋掉 CA 的檔案驗證抓取。
/// Patch 冪等（同名 rule 已存在就跳過）；備份 webconfig.bak 只在第一次寫入、不覆蓋既有備份。
/// </summary>
public sealed class WebConfigPatchService
{
    private const string RuleName = "ACME-bypass-wellknown";

    private readonly WorkRootResolver _workRoot;
    private readonly WinScpBridgeClient _bridge;

    public WebConfigPatchService(WorkRootResolver workRoot, WinScpBridgeClient bridge)
    {
        _workRoot = workRoot;
        _bridge = bridge;
    }

    /// <summary>回傳 (新XML, 是否有變更)。找不到既有 rewrite/rules 時會就地新建節點。</summary>
    internal static (string NewXml, bool Changed) AddBypassRule(string xmlText)
    {
        xmlText = xmlText.TrimStart('﻿');
        var doc = new XmlDocument { PreserveWhitespace = false };
        doc.LoadXml(xmlText);

        // ★ 一律用 local-name() 比對、不看命名空間：舊專案（VS2005 年代）的 web.config 根節點常帶
        // xmlns="http://schemas.microsoft.com/.NetConfiguration/v2.0"，IIS 不在意，但 XPath 的
        // "/configuration" 只認沒有命名空間的節點，會整份找不到。
        var config = doc.SelectSingleNode("/*[local-name()='configuration']") as XmlElement
            ?? throw new InvalidOperationException("web.config 沒有 <configuration> 根節點");

        // ★ 新建的節點要跟 <configuration> 同一個命名空間：用空命名空間建立的話，輸出時會多出
        // xmlns=""（例如 <rule xmlns="" ...>），IIS 會當成不認得的屬性，整個站台直接 500.19。
        var ns = config.NamespaceURI;
        XmlElement NewElement(string name) => doc.CreateElement(name, ns);

        // 已存在同名規則 -> 不重複加（全份搜尋，避免重複）
        var existing = doc.SelectNodes("//*[local-name()='rewrite']/*[local-name()='rules']/*[local-name()='rule']");
        if (existing is not null)
        {
            foreach (XmlElement r in existing)
            {
                if (r.GetAttribute("name") == RuleName)
                    return (xmlText, false);
            }
        }

        // 優先沿用「既有的」rewrite/rules（偏好已含 <rule> 的），避免新建出第二個 system.webServer
        XmlElement? rules = null;
        var allRules = doc.SelectNodes("//*[local-name()='system.webServer']/*[local-name()='rewrite']/*[local-name()='rules']");
        if (allRules is { Count: > 0 })
        {
            foreach (XmlElement candidate in allRules)
            {
                if (candidate.SelectNodes("*[local-name()='rule']")!.Count > 0) { rules = candidate; break; }
            }
            rules ??= (XmlElement)allRules[0]!;
        }

        if (rules is null)
        {
            var sws = doc.SelectSingleNode("//*[local-name()='system.webServer']") as XmlElement;
            if (sws is null)
            {
                sws = NewElement("system.webServer");
                config.AppendChild(sws);
            }
            var rewrite = sws.SelectSingleNode("*[local-name()='rewrite']") as XmlElement;
            if (rewrite is null)
            {
                rewrite = NewElement("rewrite");
                sws.AppendChild(rewrite);
            }
            rules = rewrite.SelectSingleNode("*[local-name()='rules']") as XmlElement;
            if (rules is null)
            {
                rules = NewElement("rules");
                rewrite.AppendChild(rules);
            }
        }

        var rule = NewElement("rule");
        rule.SetAttribute("name", RuleName);
        rule.SetAttribute("stopProcessing", "true");
        var match = NewElement("match");
        match.SetAttribute("url", @"^\.well-known/pki-validation/.*$");
        rule.AppendChild(match);
        var action = NewElement("action");
        action.SetAttribute("type", "None");
        rule.AppendChild(action);

        // ★ 規則要排在最前面才擋得住後面的轉址，但 <rules> 開頭若是 <clear/>（清掉繼承自上層的規則），
        // 排在它前面會被一起清掉，等於沒加——這種情況改插在 <clear/> 後面。
        var firstElement = rules.ChildNodes.OfType<XmlElement>().FirstOrDefault();
        if (firstElement is { LocalName: "clear" })
            rules.InsertAfter(rule, firstElement);
        else if (rules.HasChildNodes)
            rules.InsertBefore(rule, rules.FirstChild);
        else
            rules.AppendChild(rule);

        var sw = new StringWriter();
        using (var xw = new XmlTextWriter(sw) { Formatting = System.Xml.Formatting.Indented })
        {
            doc.WriteTo(xw);
        }
        return (sw.ToString(), true);
    }

    public WebConfigPatchResult Patch(DomainRecord row, string remoteConfigName = "web.config", string? workRootOverride = null)
    {
        var domain = row.CommonName.Trim();
        if (string.IsNullOrWhiteSpace(row.Ip) || !row.Ip.Any(char.IsDigit))
            return new WebConfigPatchResult(domain, WebConfigOutcome.Skipped, "IP 欄位無效");

        var folderName = DomainFolderNaming.ToFolderName(domain);
        var workDir = Path.Combine(_workRoot.Resolve(workRootOverride), folderName);
        Directory.CreateDirectory(workDir);
        var bakFile = Path.Combine(workDir, "webconfig.bak");
        var patchedFile = Path.Combine(workDir, "webconfig.patched.xml");

        var remoteDir = WinScpSessionFactory.NormalizeRemoteDir(row.FtpFolder);
        var remotePath = remoteDir + remoteConfigName;

        var tmp = Path.GetTempFileName();
        try
        {
            var getResult = _bridge.Invoke("get", row.Ip, row.FtpUser, row.FtpPass,
                remotePath: remotePath, localPath: tmp, timeoutSeconds: 20);

            if (getResult.Skipped)
                return new WebConfigPatchResult(domain, WebConfigOutcome.Skipped, $"遠端不存在 {remotePath}（此站可能無 web.config）");
            if (!getResult.Success)
                return new WebConfigPatchResult(domain, WebConfigOutcome.Failed, getResult.Message);

            // 備份直接複製下載下來的原始檔，不經過文字轉換——原檔若不是 UTF-8（例如 Big5），
            // 轉一手再存會把中文弄壞，還原時就是把壞掉的檔案傳回去。
            if (!File.Exists(bakFile))
                File.Copy(tmp, bakFile);

            var rawBytes = File.ReadAllBytes(tmp);
            var encoding = DetectEncoding(rawBytes);
            var xmlText = encoding.GetString(rawBytes);

            var (newXml, changed) = AddBypassRule(xmlText);
            if (!changed)
                return new WebConfigPatchResult(domain, WebConfigOutcome.AlreadyPatched, "放行規則已存在，無需變更");

            // 用原檔的編碼寫回去（XML 宣告會照原樣保留，兩者才對得上）。
            File.WriteAllText(patchedFile, newXml, encoding);
            var putResult = _bridge.Invoke("put", row.Ip, row.FtpUser, row.FtpPass,
                remotePath: remotePath, localPath: patchedFile, timeoutSeconds: 20);

            return putResult.Success
                ? new WebConfigPatchResult(domain, WebConfigOutcome.Patched, "已注入放行規則並傳回遠端")
                : new WebConfigPatchResult(domain, WebConfigOutcome.Failed, putResult.Message);
        }
        catch (Exception ex)
        {
            return new WebConfigPatchResult(domain, WebConfigOutcome.Failed, $"處理失敗: {ex.Message}");
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    /// <summary>
    /// 判斷 web.config 原檔的編碼：先看 BOM，沒有 BOM 再看 XML 宣告的 encoding="..."，都沒有就當 UTF-8。
    /// 回傳的 Encoding 寫檔時會照原檔有沒有 BOM 輸出（有 BOM 的原檔寫回去也帶 BOM，反之亦然）。
    /// Big5 這類舊編碼要在 Program.cs 註冊 CodePagesEncodingProvider 才拿得到；拿不到就退回 UTF-8。
    /// </summary>
    internal static Encoding DetectEncoding(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return new UnicodeEncoding(bigEndian: false, byteOrderMark: true);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return new UnicodeEncoding(bigEndian: true, byteOrderMark: true);

        // XML 宣告一定是 ASCII，取開頭一小段用 Latin1 解讀就看得到 encoding 屬性。
        var head = Encoding.Latin1.GetString(bytes, 0, Math.Min(bytes.Length, 200));
        var m = System.Text.RegularExpressions.Regex.Match(head, @"^\s*<\?xml[^>]*encoding\s*=\s*[""']([^""']+)[""']");
        if (m.Success && !m.Groups[1].Value.StartsWith("utf-8", StringComparison.OrdinalIgnoreCase))
        {
            try { return Encoding.GetEncoding(m.Groups[1].Value); }
            catch (ArgumentException) { /* 不認得的編碼名稱，退回 UTF-8 */ }
        }
        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    }

    public WebConfigPatchResult Restore(DomainRecord row, string remoteConfigName = "web.config", string? workRootOverride = null)
    {
        var domain = row.CommonName.Trim();
        var folderName = DomainFolderNaming.ToFolderName(domain);
        var workDir = Path.Combine(_workRoot.Resolve(workRootOverride), folderName);
        var bakFile = Path.Combine(workDir, "webconfig.bak");
        if (!File.Exists(bakFile))
            return new WebConfigPatchResult(domain, WebConfigOutcome.Failed, $"找不到備份 {bakFile}，無法還原");

        var remoteDir = WinScpSessionFactory.NormalizeRemoteDir(row.FtpFolder);
        var remotePath = remoteDir + remoteConfigName;

        var putResult = _bridge.Invoke("put", row.Ip, row.FtpUser, row.FtpPass,
            remotePath: remotePath, localPath: bakFile, timeoutSeconds: 20);

        return putResult.Success
            ? new WebConfigPatchResult(domain, WebConfigOutcome.Restored, "已用備份還原遠端 web.config")
            : new WebConfigPatchResult(domain, WebConfigOutcome.Failed, $"還原失敗: {putResult.Message}");
    }
}
