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

    public ValidationUploadResult Upload(Models.DomainRecord row, bool testMode, string? workRootOverride = null)
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

        var txtFile = new DirectoryInfo(targetDir).GetFiles("*.txt").FirstOrDefault();
        if (txtFile is null)
            return new ValidationUploadResult(domain, UploadOutcome.Failed, $"資料夾 {targetDir} 底下沒有 .txt 驗證檔");

        var result = _bridge.Invoke("upload-validation", row.Ip, row.FtpUser, row.FtpPass,
            remoteDir: ftpFolder, localPath: txtFile.FullName, timeoutSeconds: 30);

        return result.Success
            ? new ValidationUploadResult(domain, UploadOutcome.Success, result.Message)
            : new ValidationUploadResult(domain, UploadOutcome.Failed, result.Message);
    }
}
