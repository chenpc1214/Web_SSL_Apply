using SslWebConsole.Models;

namespace SslWebConsole.Services;

/// <summary>
/// 步驟6b（對應 8_Create_PFX.ps1）：openssl pkcs12 -export 合併 crt+key(+ca-bundle) 成 PFX。
/// 密碼預設由 PfxPasswordService 依 PFX 檔名派生（可手動覆寫）；沿用 legacy PBE-SHA1-3DES
/// 是刻意為了 Windows CryptoAPI/IIS 相容，不要改用 openssl 3.x 預設的 AES。
/// </summary>
public sealed class PfxService
{
    private readonly WorkRootResolver _workRoot;
    private readonly OpenSslRunner _openSsl;
    private readonly PfxPasswordService _pwService;
    private readonly CertificateFileRepository _fileRepo;

    public PfxService(WorkRootResolver workRoot, OpenSslRunner openSsl, PfxPasswordService pwService, CertificateFileRepository fileRepo)
    {
        _workRoot = workRoot;
        _openSsl = openSsl;
        _pwService = pwService;
        _fileRepo = fileRepo;
    }

    public async Task<PfxResult> CreateAsync(
        CertFileGroup group,
        string? outputFolder = null,
        string? manualPassword = null,
        string? workRootOverride = null,
        bool recordToDb = true)
    {
        var commonName = group.Domain;
        var folderName = DomainFolderNaming.ToFolderName(commonName);

        if (!File.Exists(group.KeyPath))
            return new PfxResult(commonName, false, $"找不到私鑰檔案: {group.KeyPath}");
        if (!File.Exists(group.CertPath))
            return new PfxResult(commonName, false, $"找不到憑證檔案: {group.CertPath}");

        string pfxPassword;
        try
        {
            pfxPassword = !string.IsNullOrWhiteSpace(manualPassword) ? manualPassword : _pwService.ComputePassword(folderName);
        }
        catch (InvalidOperationException ex)
        {
            return new PfxResult(commonName, false, ex.Message);
        }

        var friendlyName = $"{DateTime.Now:yyyyMMdd}-{commonName}";
        var workingFolder = Path.Combine(_workRoot.Resolve(workRootOverride), folderName);
        Directory.CreateDirectory(workingFolder);
        var pfxFileName = $"{folderName}.pfx";
        var pfxFile = Path.Combine(workingFolder, pfxFileName);

        var args = new List<string>
        {
            "pkcs12", "-export",
            "-certpbe", "PBE-SHA1-3DES",
            "-keypbe", "PBE-SHA1-3DES",
            "-macalg", "sha1",
            "-out", pfxFile,
            "-inkey", group.KeyPath,
            "-in", group.CertPath,
        };

        if (!string.IsNullOrWhiteSpace(group.BundlePath) && File.Exists(group.BundlePath))
        {
            args.Add("-certfile");
            args.Add(group.BundlePath);
        }

        args.Add("-passout");
        args.Add($"pass:{pfxPassword}");
        args.Add("-name");
        args.Add(friendlyName);

        var result = await _openSsl.RunAsync(args.ToArray());
        if (!result.Success)
            return new PfxResult(commonName, false, $"PFX 檔案生成失敗: {result.StdErr}");

        if (!string.IsNullOrWhiteSpace(outputFolder))
        {
            Directory.CreateDirectory(outputFolder);
            File.Copy(pfxFile, Path.Combine(outputFolder, pfxFileName), overwrite: true);
        }

        // 人工模式（其他憑證商）不存資料庫，呼叫時傳 recordToDb: false；NicSRS 照舊記錄。
        if (recordToDb) _fileRepo.TryUpsert(commonName);

        return new PfxResult(commonName, true, "PFX 產生完成", pfxFile, friendlyName);
    }
}
