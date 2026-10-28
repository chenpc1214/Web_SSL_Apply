using System.Text.Json;
using System.Text.Json.Nodes;
using SslWebConsole.Models;

namespace SslWebConsole.Services;

/// <summary>
/// 免費重簽／更新憑證（對應 9_Reissue.ps1，呼叫 /ssl/replace）：適用「訂閱狀態已經是
/// Issued、年期還沒用完」的網域——不管理由是例行半年換發還是 CSR/私鑰不見了，系統做的
/// 事完全一樣：送一份全新的 CSR，重新綁定到現有訂閱上，不另計費。
/// ★ 不用「檔案存不存在」判斷要不要顯示重簽按鈕——檔案隨時可能被封存/搬移/刪除，不可靠。
/// 但實際執行時，資料夾裡若已有完整的 CSR/私鑰（例如使用者剛在步驟2做過）就直接沿用，
/// 不會白白重做一次；只有資料夾不存在、或存在但檔案殘缺時才會刪除重建。
/// 年期是否真的還沒用完，讓 /ssl/replace 這一步的 API 回應當最後把關的人。
/// </summary>
public sealed class ReissueService
{
    private readonly WorkRootResolver _workRoot;
    private readonly NicSrsApiClient _api;
    private readonly NicSrsConfigLoader _configLoader;
    private readonly DomainRepository _domainRepo;
    private readonly CsrService _csrSvc;
    private readonly CertificateOrderRepository _orderRepo;

    public ReissueService(
        WorkRootResolver workRoot,
        NicSrsApiClient api,
        NicSrsConfigLoader configLoader,
        DomainRepository domainRepo,
        CsrService csrSvc,
        CertificateOrderRepository orderRepo)
    {
        _workRoot = workRoot;
        _api = api;
        _configLoader = configLoader;
        _domainRepo = domainRepo;
        _csrSvc = csrSvc;
        _orderRepo = orderRepo;
    }

    public async Task<OrderPlacementResult> ReissueAsync(string commonName, string dcvMethod, string? workRootOverride = null)
    {
        var domain = _domainRepo.LoadAll().FirstOrDefault(d => string.Equals(d.CommonName, commonName, StringComparison.OrdinalIgnoreCase));
        if (domain is null)
            return new OrderPlacementResult(commonName, false, true, "資料庫裡找不到這個網域的資料，請先到步驟1確認資料存在。");

        var folderName = DomainFolderNaming.ToFolderName(commonName);
        var workDir = Path.Combine(_workRoot.Resolve(workRootOverride), folderName);
        var csrFile = Path.Combine(workDir, $"{folderName}_CSR.csr");
        var certIdFile = Path.Combine(workDir, $"{folderName}.certid");

        // 資料夾裡已經有完整的 CSR/私鑰（例如使用者剛在步驟2做過）就直接沿用，不重新產生
        // 白做工；只有資料夾不存在、或存在但檔案殘缺時才刪除重建。
        CsrResult csrResult;
        if (File.Exists(csrFile) && File.Exists(Path.Combine(workDir, $"{folderName}.key")))
        {
            csrResult = new CsrResult { CommonName = commonName, Success = true, WorkDir = workDir, CsrFile = csrFile };
        }
        else
        {
            if (Directory.Exists(workDir))
                _csrSvc.DeleteAndRestart(commonName, workRootOverride);

            csrResult = await _csrSvc.GenerateAsync(
                commonName,
                string.IsNullOrWhiteSpace(domain.Organization) ? "IT" : domain.Organization,
                organizationalUnit: "IT",
                locality: domain.Locality,
                state: domain.Locality,
                country: "TW",
                workRootOverride: workRootOverride);
            if (!csrResult.Success)
                return new OrderPlacementResult(commonName, false, false, $"重新產生 CSR/私鑰失敗: {csrResult.Message}");
        }

        var certId = await ResolveCertIdAsync(commonName, certIdFile);
        if (string.IsNullOrWhiteSpace(certId))
            return new OrderPlacementResult(commonName, false, true, "找不到這個網域現有的 certId（本機沒有、雲端 /ssl/list 也查不到），無法重簽。");

        var cfg = _configLoader.LoadRequireApiToken();
        if (cfg.Administrator is null)
            return new OrderPlacementResult(commonName, false, true, "config 缺少 Administrator 區塊，請至公用設定填寫聯絡人資訊。");

        var csrText = await File.ReadAllTextAsync(csrFile);

        var paramsObj = new PlaceOrderParams
        {
            Server = "other",
            Csr = csrText,
            DcvMethod = dcvMethod,
            DomainInfo = [new DomainInfoEntry { DomainName = commonName, DcvMethod = dcvMethod }],
            Administrator = cfg.Administrator,
        };
        var paramsJson = JsonSerializer.Serialize(paramsObj);

        // ── /ssl/replace：免費，但年期真的用完的話 NicSRS 會在這裡直接回錯誤 ──
        var replace = await _api.CallAsync("/ssl/replace", new Dictionary<string, string>
        {
            ["certId"] = certId,
            ["params"] = paramsJson,
            ["csr"] = csrText,
        });
        if (replace is null)
            return new OrderPlacementResult(commonName, false, false, "重簽失敗（API 無回應）。");
        if ((int?)replace["code"] != 1)
            return new OrderPlacementResult(commonName, false, false, $"重簽失敗 (code={replace["code"]}): {PlaceOrderService.FormatApiError(replace)}");

        var newCertId = replace["data"]?["certId"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(newCertId)) newCertId = certId;

        if (newCertId != certId && File.Exists(certIdFile))
            await File.WriteAllTextAsync(Path.Combine(workDir, $"{folderName}.certid.old"), certId);
        await File.WriteAllTextAsync(certIdFile, newCertId);
        await File.WriteAllTextAsync(Path.Combine(workDir, $"{folderName}.replace.json"), replace.ToJsonString());

        PlaceOrderService.WriteDcvFiles(commonName, workDir, dcvMethod, replace["data"]);
        _orderRepo.RecordReissued(commonName, newCertId);

        return new OrderPlacementResult(commonName, true, false, $"重簽成功！certId = {newCertId}（免費，不會扣款）", newCertId);
    }

    /// <summary>
    /// 純粹查 NicSRS 這個網域名下有沒有任何訂閱紀錄，不下載、不處理，給步驟1存檔時判斷
    /// 「這是不是資料庫沒追蹤過、但 NicSRS 那邊其實已經有訂閱的舊網域」用。
    /// </summary>
    public async Task<bool> HasExistingSubscriptionAsync(string commonName)
    {
        var res = await _api.CallAsync("/ssl/list", new Dictionary<string, string> { ["domainName"] = commonName });
        if (res is null || (int?)res["code"] != 1) return false;

        var items = res["data"] switch
        {
            JsonArray arr => arr.Where(x => x is not null),
            JsonNode single => [single],
            null => Enumerable.Empty<JsonNode>(),
        };
        return items.Any();
    }

    /// <summary>比照 9_Reissue.ps1 的 Resolve-CertId：先讀本機 .certid，讀不到才查 /ssl/list 反查。</summary>
    private async Task<string?> ResolveCertIdAsync(string commonName, string certIdFile)
    {
        if (File.Exists(certIdFile))
        {
            var local = (await File.ReadAllTextAsync(certIdFile)).Trim();
            if (!string.IsNullOrWhiteSpace(local)) return local;
        }

        var res = await _api.CallAsync("/ssl/list", new Dictionary<string, string> { ["domainName"] = commonName });
        if (res is null || (int?)res["code"] != 1) return null;

        var items = res["data"] switch
        {
            JsonArray arr => arr.Where(x => x is not null).Select(x => x!).ToList(),
            JsonNode single => [single],
            null => new List<JsonNode>(),
        };
        if (items.Count == 0) return null;

        var issuedStatuses = new[] { "COMPLETE", "REISSUED", "RENEWED" };
        var pick = items.FirstOrDefault(i => issuedStatuses.Contains(i["status"]?.GetValue<string>())) ?? items[0];
        return pick["certId"]?.GetValue<string>();
    }
}
