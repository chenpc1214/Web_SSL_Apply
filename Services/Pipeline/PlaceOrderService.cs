using System.Text.Json;
using System.Text.Json.Nodes;
using SslWebConsole.Models;

namespace SslWebConsole.Services;

/// <summary>
/// 步驟3（對應 4_Place_Order.ps1）：呼叫 NicSRS API 下單，取得 certId 與驗證檔。
/// ★ PlaceOrderAsync 的 /ssl/place 呼叫會真的扣款——呼叫前必須通過 CheckExistingOrderAsync
/// 的防重複檢查（或呼叫端明確帶 force=true）。這支服務本身不做任何「安全網」以外的攔阻，
/// UI 端務必在按鈕前加二次確認。
/// </summary>
public sealed class PlaceOrderService
{
    public static readonly string[] AllowedProductCodes = ["essential-dv-ssl", "comodo-positivessl"];

    private readonly WorkRootResolver _workRoot;
    private readonly NicSrsApiClient _api;
    private readonly NicSrsConfigLoader _configLoader;
    private readonly CertificateOrderRepository _orderRepo;

    public PlaceOrderService(WorkRootResolver workRoot, NicSrsApiClient api, NicSrsConfigLoader configLoader, CertificateOrderRepository orderRepo)
    {
        _workRoot = workRoot;
        _api = api;
        _configLoader = configLoader;
        _orderRepo = orderRepo;
    }

    /// <summary>對應 Resolve-OrderSpec：驗證 ProductCode 在允許清單、Years 是 1~6 的整數。</summary>
    public static OrderSpecResult ResolveOrderSpec(string? productCode, string? years)
    {
        var code = (productCode ?? "").Trim();
        if (string.IsNullOrWhiteSpace(code))
            return new OrderSpecResult(null, null, $"未填 ProductCode（方案）。可填： {string.Join(" / ", AllowedProductCodes)}");
        if (!AllowedProductCodes.Contains(code))
            return new OrderSpecResult(null, null, $"ProductCode '{code}' 不在允許清單。可填： {string.Join(" / ", AllowedProductCodes)}");

        if (!int.TryParse((years ?? "").Trim(), out var yearsInt) || yearsInt <= 0 || yearsInt > 6)
            return new OrderSpecResult(null, null, $"Years（年數）'{years}' 無效，須為 1~6 的整數。");

        return new OrderSpecResult(code, yearsInt, null);
    }

    /// <summary>對應 Test-ExistingOrder：查 NicSRS 該網域是否已有進行中/未到續期窗口的有效訂單。回傳非 null 代表「應該擋下、不要下單」。</summary>
    public async Task<ExistingOrderInfo?> CheckExistingOrderAsync(string commonName, int renewWithinDays = 30)
    {
        var res = await _api.CallAsync("/ssl/list", new Dictionary<string, string> { ["domainName"] = commonName });
        if (res is null || (int?)res["code"] != 1)
            return null; // 查詢失敗：保守起見不阻擋（跟原腳本一致），但呼叫端應該提示使用者留意

        var items = NormalizeToList(res["data"]);
        var want = commonName.Trim().ToLowerInvariant();

        items = items.Where(item =>
        {
            var cn = (item["commonName"]?.GetValue<string>() ?? "").Trim().ToLowerInvariant();
            if (cn == want) return true;
            var domains = item["domains"] as JsonArray;
            return domains?.Any(d => (d?.GetValue<string>() ?? "").Trim().ToLowerInvariant() == want) ?? false;
        }).ToList();

        if (items.Count == 0) return null;

        var pending = items.FirstOrDefault(i => i["status"]?.GetValue<string>() == "PENDING");
        if (pending is not null)
            return new ExistingOrderInfo(pending["certId"]?.GetValue<string>(), "PENDING", null, null);

        var issuedStatuses = new[] { "COMPLETE", "REISSUED", "RENEWED" };
        foreach (var item in items.Where(i => issuedStatuses.Contains(i["status"]?.GetValue<string>())))
        {
            var endDateStr = item["endDate"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(endDateStr) || !DateTime.TryParse(endDateStr, out var endDate))
            {
                // 沒有可解析的到期日 → 保守視為「有效、擋下」
                return new ExistingOrderInfo(item["certId"]?.GetValue<string>(), item["status"]?.GetValue<string>(), null, null);
            }

            var daysLeft = (int)(endDate - DateTime.Now).TotalDays;
            if (daysLeft > renewWithinDays)
                return new ExistingOrderInfo(item["certId"]?.GetValue<string>(), item["status"]?.GetValue<string>(), endDate, daysLeft);
        }

        // 所有已簽發的都快到期/已過期 → 放行續期
        return null;
    }

    /// <summary>
    /// 對應 Invoke-PlaceOrder。呼叫 /ssl/validate 再 /ssl/place——/ssl/place ★會真的扣款★。
    /// force=true 會跳過本機 .certid 檢查與雲端查重（緊急續期/重買用，等同 -Force）。
    /// </summary>
    public async Task<OrderPlacementResult> PlaceOrderAsync(
        string commonName,
        string productCode,
        int years,
        string dcvMethod,
        bool force = false,
        int renewWithinDays = 30,
        string? workRootOverride = null)
    {
        var folderName = DomainFolderNaming.ToFolderName(commonName);
        var workDir = Path.Combine(_workRoot.Resolve(workRootOverride), folderName);
        var csrFile = Path.Combine(workDir, $"{folderName}_CSR.csr");
        var certIdFile = Path.Combine(workDir, $"{folderName}.certid");

        if (!Directory.Exists(workDir))
            return new OrderPlacementResult(commonName, false, true, $"找不到工作資料夾: {workDir}（請先執行步驟2產生 CSR）");
        if (!File.Exists(csrFile))
            return new OrderPlacementResult(commonName, false, true, $"找不到 CSR: {csrFile}（請先執行步驟2）");

        if (File.Exists(certIdFile) && !force)
            return new OrderPlacementResult(commonName, false, true, $"此網域本機已有 .certid（{certIdFile}）。要續期/重買請勾選「強制下單」。");

        if (!force)
        {
            var existing = await CheckExistingOrderAsync(commonName, renewWithinDays);
            if (existing is not null)
            {
                if (!string.IsNullOrWhiteSpace(existing.CertId))
                    await File.WriteAllTextAsync(certIdFile, existing.CertId);

                var daysMsg = existing.DaysLeft is not null ? $"，距到期還 {existing.DaysLeft} 天" : "";
                return new OrderPlacementResult(commonName, false, true,
                    $"此網域在 NicSRS 已有有效訂單（certId={existing.CertId}, status={existing.Status}{daysMsg}），不重複下單。", existing.CertId);
            }
        }

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

        var validate = await _api.CallAsync("/ssl/validate", new Dictionary<string, string>
        {
            ["productCode"] = productCode,
            ["years"] = years.ToString(),
            ["params"] = paramsJson,
            ["csr"] = csrText,
        });
        if (validate is null)
            return new OrderPlacementResult(commonName, false, false, "驗證訂單參數失敗（API 無回應）。");
        if ((int?)validate["code"] != 1)
            return new OrderPlacementResult(commonName, false, false, $"驗證未通過 (code={validate["code"]}): {FormatApiError(validate)}");

        // ── /ssl/place：真的扣款 ──────────────────────────────
        var place = await _api.CallAsync("/ssl/place", new Dictionary<string, string>
        {
            ["productCode"] = productCode,
            ["years"] = years.ToString(),
            ["params"] = paramsJson,
            ["csr"] = csrText,
        });
        if (place is null)
            return new OrderPlacementResult(commonName, false, false, "下單失敗（API 無回應）。");
        if ((int?)place["code"] != 1)
            return new OrderPlacementResult(commonName, false, false, $"下單失敗 (code={place["code"]}): {FormatApiError(place)}");

        var certId = place["data"]?["certId"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(certId))
            return new OrderPlacementResult(commonName, false, false, "下單回應沒有 certId。");

        await File.WriteAllTextAsync(certIdFile, certId);
        await File.WriteAllTextAsync(Path.Combine(workDir, $"{folderName}.order.json"), place.ToJsonString());

        WriteDcvFiles(commonName, workDir, dcvMethod, place["data"]);
        _orderRepo.RecordPlaced(commonName, certId);

        return new OrderPlacementResult(commonName, true, false, $"下單成功！certId = {certId}", certId);
    }

    /// <summary>公開給 ReissueService（/ssl/replace 回應的 dcvInfo 結構一致）複用。</summary>
    public static void WriteDcvFiles(string commonName, string workDir, string dcvMethod, JsonNode? data)
    {
        if (data is null) return;

        var node = data;
        var dcvInfo = data["dcvInfo"] as JsonObject;
        if (dcvInfo is not null)
        {
            var match = dcvInfo.FirstOrDefault(kv => string.Equals(kv.Key, commonName, StringComparison.OrdinalIgnoreCase));
            node = match.Value ?? dcvInfo.First().Value;
        }

        if (dcvMethod.StartsWith("HTTP", StringComparison.OrdinalIgnoreCase))
        {
            var fileName = node?["DCVfileName"]?.GetValue<string>();
            var content = node?["DCVfileContent"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(fileName)) return;
            File.WriteAllText(Path.Combine(workDir, fileName), content ?? "", new System.Text.UTF8Encoding(false));
        }
        else if (dcvMethod.StartsWith("DNS", StringComparison.OrdinalIgnoreCase))
        {
            var host = node?["DCVdnsHost"]?.GetValue<string>();
            var value = node?["DCVdnsValue"]?.GetValue<string>();
            var type = node?["DCVdnsType"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(value))
            {
                host = node?["DCVdnsTXTHost"]?.GetValue<string>();
                value = node?["DCVdnsTXTValue"]?.GetValue<string>();
                type = "TXT";
            }
            var rec = $"Type : {type}\nHost : {host}\nValue: {value}\n";
            File.WriteAllText(Path.Combine(workDir, "_dns_record.txt"), rec, new System.Text.UTF8Encoding(false));
        }
        else if (dcvMethod.StartsWith("CNAME", StringComparison.OrdinalIgnoreCase))
        {
            var rec = $"Type : CNAME\nHost : {node?["DCVdnsHost"]?.GetValue<string>()}\nValue: {node?["DCVdnsValue"]?.GetValue<string>()}\n";
            File.WriteAllText(Path.Combine(workDir, "_dns_record.txt"), rec, new System.Text.UTF8Encoding(false));
        }
        // EMAIL：無需落地檔案，使用者需至驗證信箱點擊確認連結
    }

    /// <summary>公開給 ReissueService 複用，格式化 NicSRS 錯誤回應。</summary>
    public static string FormatApiError(JsonNode response)
    {
        var parts = new List<string>();
        foreach (var field in new[] { "message", "msg", "errorMsg", "error" })
        {
            var v = response[field]?.ToString();
            if (!string.IsNullOrWhiteSpace(v)) parts.Add($"{field}={v}");
        }
        if (response["errors"] is not null)
            parts.Add("errors=" + response["errors"]!.ToJsonString());
        return parts.Count > 0 ? string.Join(" | ", parts) : response.ToJsonString();
    }

    private static List<JsonNode> NormalizeToList(JsonNode? data) => data switch
    {
        JsonArray arr => arr.Where(x => x is not null).Select(x => x!).ToList(),
        JsonNode single => [single],
        null => [],
    };
}
