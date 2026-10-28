namespace SslWebConsole.Services;

public sealed record OrderStatusInfo(string CommonName, bool HasCertId, string? CertId, string CertIdFilePath);

/// <summary>
/// 佔位服務：第3步(下單)本次不重寫，仍由既有 4_Place_Order.ps1 在終端機手動執行。
/// 這裡只偵測 <FolderName>.certid 是否已經產生，供步驟4/5 判斷能不能往下走。
/// </summary>
public sealed class OrderStatusService
{
    private readonly WorkRootResolver _workRoot;

    public OrderStatusService(WorkRootResolver workRoot)
    {
        _workRoot = workRoot;
    }

    public OrderStatusInfo Check(string commonName, string? workRootOverride = null)
    {
        var folderName = DomainFolderNaming.ToFolderName(commonName);
        var workDir = Path.Combine(_workRoot.Resolve(workRootOverride), folderName);
        var certIdFile = Path.Combine(workDir, $"{folderName}.certid");

        if (!File.Exists(certIdFile))
            return new OrderStatusInfo(commonName, false, null, certIdFile);

        var certId = File.ReadAllText(certIdFile).Trim();
        return new OrderStatusInfo(commonName, !string.IsNullOrWhiteSpace(certId), certId, certIdFile);
    }

    /// <summary>這個網域資料夾裡有沒有重簽留下的 .replace.json（ReissueService 成功送出 /ssl/replace 時寫的）。
    /// 步驟5 用來分辨「新購訂單」跟「重簽」——只有新購訂單能作廢，重簽沒有新訂單可以作廢。</summary>
    public bool HasReissueRecord(string commonName, string? workRootOverride = null)
    {
        var folderName = DomainFolderNaming.ToFolderName(commonName);
        var workDir = Path.Combine(_workRoot.Resolve(workRootOverride), folderName);
        return File.Exists(Path.Combine(workDir, $"{folderName}.replace.json"));
    }

    /// <summary>
    /// 作廢訂單時把本機 .certid 改名成 .certid.cancelled（已經有就加時間），保留紀錄但不再被當成
    /// 有效訂單——下單（PlaceOrderService 看到 .certid 會擋）、步驟3勾選、步驟5輪詢都靠這個檔案。
    /// CSR、私鑰、.order.json 都不動，重新下單可以直接沿用同一組 CSR。回傳改名後的檔名，沒有 .certid 就回 null。
    /// </summary>
    public string? CancelLocalCertId(string commonName, string? workRootOverride = null)
    {
        var info = Check(commonName, workRootOverride);
        if (!File.Exists(info.CertIdFilePath)) return null;

        var target = info.CertIdFilePath + ".cancelled";
        if (File.Exists(target))
            target = $"{info.CertIdFilePath}.cancelled_{DateTime.Now:yyyyMMdd_HHmmss}";

        File.Move(info.CertIdFilePath, target);
        return Path.GetFileName(target);
    }
}
