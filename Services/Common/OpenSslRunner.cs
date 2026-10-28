using System.Diagnostics;

namespace SslWebConsole.Services;

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Success => ExitCode == 0;
}

/// <summary>
/// 共用 openssl.exe 呼叫器。沿用 3_Generate_CSR.ps1 / 8_Create_PFX.ps1 同一支 openssl.exe，
/// 不改用原生 .NET 加密 API，確保產出格式（尤其 PFX 的 legacy PBE-SHA1-3DES）與既有流程一致。
/// </summary>
public sealed class OpenSslRunner
{
    public const string OpenSslPath = @"C:\Program Files\OpenSSL-Win64\bin\openssl.exe";

    public async Task<ProcessResult> RunAsync(params string[] args)
    {
        var psi = new ProcessStartInfo(OpenSslPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        // 清掉可能殘留、指向不存在檔案的 OPENSSL_CONF（僅影響這個子行程，不動系統環境變數）。
        psi.Environment.Remove("OPENSSL_CONF");

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("無法啟動 openssl.exe");
        var stdOutTask = process.StandardOutput.ReadToEndAsync();
        var stdErrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new ProcessResult(process.ExitCode, await stdOutTask, await stdErrTask);
    }
}
