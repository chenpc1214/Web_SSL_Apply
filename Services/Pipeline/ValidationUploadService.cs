using SslWebConsole.Models;

namespace SslWebConsole.Services;

/// <summary>
/// 步驟4b（對應 2_Upload_Validation.ps1）：把網域資料夾下第一個 .txt 驗證檔上傳到
/// FTP 遠端 .well-known/pki-validation/。testMode 只測連線與遠端資料夾是否存在，不上傳。
/// </summary>
public sealed class ValidationUploadService
{
    private readonly WorkRootResolver _workRoot;
    private readonly WinScpBridgeClient _bridge;

    public ValidationUploadService(WorkRootResolver workRoot, WinScpBridgeClient bridge)
    {
        _workRoot = workRoot;
        _bridge = bridge;
    }

    /// <param name="requireSingleTxt">人工模式用：驗證檔是使用者手動放進資料夾的，有兩個以上的 .txt
    /// 就不知道該傳哪個，乾脆不傳並列出檔名。NicSRS 的驗證檔由程式寫出，維持「取第一個」。</param>
    public ValidationUploadResult Upload(Models.DomainRecord row, bool testMode, string? workRootOverride = null, bool requireSingleTxt = false)
    {
        var domain = row.CommonName.Trim();
        var folderName = DomainFolderNaming.ToFolderName(domain);
        var targetDir = Path.Combine(_workRoot.Resolve(workRootOverride), folderName);
        var ftpFolder = WinScpSessionFactory.NormalizeRemoteDir(row.FtpFolder);

        if (testMode)
        {
            var testResult = _bridge.Invoke("test", row.Ip, row.FtpUser, row.FtpPass, remoteDir: ftpFolder);
            return testResult.Success
                ? new ValidationUploadResult(domain, UploadOutcome.Success, testResult.Message)
                : new ValidationUploadResult(domain, UploadOutcome.Failed, testResult.Message);
        }

        if (!Directory.Exists(targetDir))
            return new ValidationUploadResult(domain, UploadOutcome.Skipped, $"找不到對應的資料夾: {targetDir}");

        var txtFiles = new DirectoryInfo(targetDir).GetFiles("*.txt");
        if (requireSingleTxt && txtFiles.Length > 1)
            return new ValidationUploadResult(domain, UploadOutcome.Failed,
                $"資料夾裡有 {txtFiles.Length} 個 .txt（{string.Join("、", txtFiles.Select(f => f.Name))}），不知道該傳哪個，請只留下驗證檔");
        var txtFile = txtFiles.FirstOrDefault();
        if (txtFile is null)
            return new ValidationUploadResult(domain, UploadOutcome.Failed, $"資料夾 {targetDir} 底下沒有 .txt 驗證檔");

        var result = _bridge.Invoke("upload-validation", row.Ip, row.FtpUser, row.FtpPass,
            remoteDir: ftpFolder, localPath: txtFile.FullName, timeoutSeconds: 30);

        return result.Success
            ? new ValidationUploadResult(domain, UploadOutcome.Success, result.Message)
            : new ValidationUploadResult(domain, UploadOutcome.Failed, result.Message);
    }
}
