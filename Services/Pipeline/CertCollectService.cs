using System.Text;
using SslWebConsole.Models;

namespace SslWebConsole.Services;

/// <summary>
/// 步驟5（對應 6_Collect_Cert.ps1）：回合制輪詢佇列，每輪只查「尚未完成」的網域，
/// COMPLETE/FAILED 立即移出佇列、落地 .crt/.ca-bundle；PENDING/ERROR 留到下一輪。
/// 單一卡住的網域不會擋住其他網域。
/// </summary>
public sealed class CertCollectService
{
    private readonly WorkRootResolver _workRoot;
    private readonly NicSrsApiClient _api;
    private readonly NicSrsConfigLoader _configLoader;
    private readonly CertificateOrderRepository _orderRepo;

    public CertCollectService(WorkRootResolver workRoot, NicSrsApiClient api, NicSrsConfigLoader configLoader, CertificateOrderRepository orderRepo)
    {
        _workRoot = workRoot;
        _api = api;
        _configLoader = configLoader;
        _orderRepo = orderRepo;
    }

    public async Task<CertPollOutcome> CheckOnceAsync(string commonName, string? certId, string? workRootOverride = null)
    {
        var folderName = DomainFolderNaming.ToFolderName(commonName);
        var workDir = Path.Combine(_workRoot.Resolve(workRootOverride), folderName);
        var certIdFile = Path.Combine(workDir, $"{folderName}.certid");

        if (string.IsNullOrWhiteSpace(certId))
        {
            if (!File.Exists(certIdFile))
                return new CertPollOutcome(commonName, CertPollStatus.Error, $"找不到 certId 檔: {certIdFile}（請先於終端機執行 4_Place_Order.ps1 下單）");
            certId = File.ReadAllText(certIdFile).Trim();
        }

        var res = await _api.CallAsync("/ssl/collect", new Dictionary<string, string> { ["certId"] = certId });
        if (res is null)
            return new CertPollOutcome(commonName, CertPollStatus.Error, "API 呼叫失敗（逾時/連線/回應非 JSON）");

        var code = res["code"]?.GetValue<int?>();
        if (code == 2)
            return new CertPollOutcome(commonName, CertPollStatus.Pending, "CA 簽發中...");
        if (code != 1)
            return new CertPollOutcome(commonName, CertPollStatus.Error, $"查詢失敗 code={code}: {res["errors"]?.ToJsonString()}");

        var status = res["status"]?.GetValue<string>() ?? "";
        switch (status)
        {
            case "COMPLETE":
                var crt = res["data"]?["certificate"]?.GetValue<string>();
                var ca = res["data"]?["caCertificate"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(crt))
                    return new CertPollOutcome(commonName, CertPollStatus.Pending, "狀態 COMPLETE 但回應無 certificate");

                Directory.CreateDirectory(workDir);
                WritePem(Path.Combine(workDir, $"{folderName}.crt"), crt);
                var caMsg = "";
                if (!string.IsNullOrWhiteSpace(ca))
                {
                    WritePem(Path.Combine(workDir, $"{folderName}.ca-bundle"), ca);
                }
                else
                {
                    caMsg = "（回應無 caCertificate，部分 DV 可能不含）";
                }

                DateTime? expiresAt = null;
                if (DateTime.TryParse(res["data"]?["endDate"]?.GetValue<string>(), out var parsedEnd))
                    expiresAt = parsedEnd;
                _orderRepo.RecordIssued(commonName, certId, expiresAt);

                return new CertPollOutcome(commonName, CertPollStatus.Complete, $"憑證已落地: {folderName}.crt {caMsg}".Trim());

            case "FAILED":
                var reason = res["data"]?["failReason"]?.ToJsonString() ?? "";
                _orderRepo.RecordFailed(commonName, certId);
                return new CertPollOutcome(commonName, CertPollStatus.Failed, $"簽發失敗: {reason}");

            default:
                var dcv = res["data"]?["dcv"]?["status"]?.ToString() ?? "";
                var iss = res["data"]?["issued"]?["status"]?.ToString() ?? "";
                return new CertPollOutcome(commonName, CertPollStatus.Pending, $"status={status} (dcv={dcv}, issued={iss})");
        }
    }

    /// <summary>
    /// 回合制輪詢直到全部完成、達 PollMaxMinutes 上限、或 once=true 只掃一輪。
    /// onOutcome 在每個網域查完當下觸發（可用來即時更新 Blazor 畫面）。
    /// ★ once=false 時，每一輪查詢前都先倒數等待（含第一輪）——憑證簽發不會馬上完成，
    /// 一開始就查多半只是白跑一趟 API；once=true（使用者手動「只查一次」）則不等，立刻查。
    /// onCountdown 每秒觸發一次剩餘秒數（0 代表倒數結束/不在等待中），給畫面顯示即時倒數用，
    /// 不是像 onRoundInfo 那樣寫進輪詢紀錄——每秒都記一行 log 會洗版。
    /// </summary>
    public async Task PollQueueAsync(
        IReadOnlyList<(string Name, string? CertId)> queue,
        bool once,
        Action<CertPollOutcome> onOutcome,
        Action<string>? onRoundInfo,
        CancellationToken ct,
        string? workRootOverride = null,
        Action<int>? onCountdown = null)
    {
        var cfg = _configLoader.Load();
        var interval = TimeSpan.FromSeconds(Math.Max(1, cfg.PollIntervalSeconds));
        var deadline = DateTime.Now.AddMinutes(cfg.PollMaxMinutes);
        var done = new HashSet<string>();

        while (!ct.IsCancellationRequested)
        {
            var pending = queue.Where(q => !done.Contains(q.Name)).ToList();
            if (pending.Count == 0) break;

            if (!once)
            {
                if (DateTime.Now >= deadline)
                {
                    onRoundInfo?.Invoke($"已達輪詢上限 {cfg.PollMaxMinutes} 分鐘，仍有 {pending.Count} 個網域未完成。");
                    break;
                }

                await WaitWithCountdownAsync(interval, onCountdown, ct);
                if (ct.IsCancellationRequested) break;
            }

            foreach (var q in pending)
            {
                if (ct.IsCancellationRequested) break;
                var outcome = await CheckOnceAsync(q.Name, q.CertId, workRootOverride);
                if (outcome.Status is CertPollStatus.Complete or CertPollStatus.Failed)
                    done.Add(q.Name);
                onOutcome(outcome);
            }

            if (once || queue.All(q => done.Contains(q.Name))) break;
        }
    }

    private static async Task WaitWithCountdownAsync(TimeSpan interval, Action<int>? onCountdown, CancellationToken ct)
    {
        var remaining = (int)Math.Ceiling(interval.TotalSeconds);
        while (remaining > 0 && !ct.IsCancellationRequested)
        {
            onCountdown?.Invoke(remaining);
            try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { break; }
            remaining--;
        }
        onCountdown?.Invoke(0);
    }

    private static void WritePem(string path, string content)
    {
        var normalized = content.Replace("\r\n", "\n");
        if (!normalized.EndsWith('\n')) normalized += "\n";
        File.WriteAllText(path, normalized, new UTF8Encoding(false));
    }
}
