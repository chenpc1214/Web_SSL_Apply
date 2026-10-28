using SslWebConsole.Models;

namespace SslWebConsole.Services;

/// <summary>
/// 步驟2（對應 3_Generate_CSR.ps1）：openssl genrsa + req -new 產生私鑰/CSR。
/// 資料夾已存在時拒絕覆蓋，改由 DeleteAndRestart 明確刪除後才能重來（原腳本註解點名要做的網頁功能）。
/// </summary>
public sealed class CsrService
{
    private readonly WorkRootResolver _workRoot;
    private readonly OpenSslRunner _openSsl;

    public CsrService(WorkRootResolver workRoot, OpenSslRunner openSsl)
    {
        _workRoot = workRoot;
        _openSsl = openSsl;
    }

    public async Task<CsrResult> GenerateAsync(
        string commonName,
        string organization,
        string organizationalUnit,
        string locality,
        string? state,
        string country = "TW",
        int keyLength = 4096,
        string? workRootOverride = null)
    {
        commonName = commonName.Trim();
        var folderName = DomainFolderNaming.ToFolderName(commonName);
        var root = _workRoot.Resolve(workRootOverride);
        var workDir = Path.Combine(root, folderName);
        var keyFile = Path.Combine(workDir, $"{folderName}.key");
        var csrFile = Path.Combine(workDir, $"{folderName}_CSR.csr");

        if (Directory.Exists(workDir))
        {
            return new CsrResult
            {
                CommonName = commonName,
                Success = false,
                AlreadyExists = true,
                Message = $"資料夾已存在: {workDir}（請用「刪除並重新開始」再重新產生）",
                WorkDir = workDir,
            };
        }

        Directory.CreateDirectory(workDir);

        var effectiveState = string.IsNullOrWhiteSpace(state) ? locality : state;
        var subject = $"/CN={commonName}/O={organization}/OU={organizationalUnit}/L={locality}/ST={effectiveState}/C={country}";

        var genRsa = await _openSsl.RunAsync("genrsa", "-out", keyFile, keyLength.ToString());
        if (!genRsa.Success)
        {
            return new CsrResult
            {
                CommonName = commonName,
                Success = false,
                Message = $"私鑰生成失敗: {genRsa.StdErr}",
                WorkDir = workDir,
            };
        }

        var req = await _openSsl.RunAsync("req", "-new", "-key", keyFile, "-out", csrFile, "-subj", subject);
        if (!req.Success)
        {
            return new CsrResult
            {
                CommonName = commonName,
                Success = false,
                Message = $"CSR 生成失敗: {req.StdErr}",
                WorkDir = workDir,
            };
        }

        return new CsrResult
        {
            CommonName = commonName,
            Success = true,
            Message = "CSR 產生完成",
            WorkDir = workDir,
            KeyFile = keyFile,
            CsrFile = csrFile,
        };
    }

    /// <summary>「刪除並重新開始」：整個網域資料夾刪除，讓使用者能重跑 GenerateAsync。會連同私鑰一起刪除，呼叫前務必再次確認。</summary>
    public void DeleteAndRestart(string commonName, string? workRootOverride = null)
    {
        var folderName = DomainFolderNaming.ToFolderName(commonName);
        var workDir = Path.Combine(_workRoot.Resolve(workRootOverride), folderName);
        if (Directory.Exists(workDir))
            Directory.Delete(workDir, recursive: true);
    }

    public bool DomainFolderExists(string commonName, string? workRootOverride = null) =>
        Directory.Exists(_workRoot.DomainDir(commonName, workRootOverride));
}
