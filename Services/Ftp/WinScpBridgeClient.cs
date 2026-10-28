using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace SslWebConsole.Services;

public sealed record WinScpBridgeResult(bool Success, bool Skipped, string Message, string? LocalPath);

/// <summary>
/// 透過 PowerShell（WinScpBridge.ps1）操作 WinSCP，取代直接在 .NET 10 行程內呼叫
/// WinSCPnet.dll ——後者會讓 WinSCP.com 子行程原生當機（0xC0000005）。PowerShell 5.1
/// 執行環境跟 .ps1 腳本一樣，證實可穩定呼叫同一支 WinSCPnet.dll。
/// </summary>
public sealed class WinScpBridgeClient
{
    // 隨組件輸出到 bin 資料夾（見 SslWebConsole.csproj 的 CopyToOutputDirectory 設定），
    // 不放 ScriptsRoot——這是網頁程式自己的實作細節，不是給使用者在終端機執行的腳本。
    private static readonly string ScriptPath = Path.Combine(AppContext.BaseDirectory, "Scripts", "WinScpBridge.ps1");

    public WinScpBridgeResult Invoke(
        string action,
        string ip,
        string user,
        string pass,
        string? remoteDir = null,
        string? remotePath = null,
        string? localPath = null,
        int timeoutSeconds = 15)
    {
        if (!File.Exists(ScriptPath))
            return new WinScpBridgeResult(false, false, $"找不到橋接腳本: {ScriptPath}", null);

        var requestJson = JsonSerializer.Serialize(new
        {
            Action = action,
            Ip = ip,
            User = user,
            Password = pass,
            RemoteDir = remoteDir,
            RemotePath = remotePath,
            LocalPath = localPath,
            TimeoutSeconds = timeoutSeconds,
        });

        var utf8NoBom = new UTF8Encoding(false);
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = utf8NoBom,
            StandardOutputEncoding = utf8NoBom,
            StandardErrorEncoding = utf8NoBom,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(ScriptPath);

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        using var process = new Process { StartInfo = psi };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.StandardInput.WriteLine(requestJson);
        process.StandardInput.Close();

        var exited = process.WaitForExit((timeoutSeconds + 20) * 1000);
        if (!exited)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            return new WinScpBridgeResult(false, false, "PowerShell 橋接逾時無回應", null);
        }

        var lastLine = stdout.ToString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault();

        if (string.IsNullOrWhiteSpace(lastLine))
        {
            var errText = stderr.ToString().Trim();
            return new WinScpBridgeResult(false, false,
                string.IsNullOrEmpty(errText) ? "PowerShell 橋接沒有回應輸出" : $"PowerShell 橋接錯誤: {errText}", null);
        }

        try
        {
            using var doc = JsonDocument.Parse(lastLine);
            var root = doc.RootElement;
            var success = root.TryGetProperty("success", out var s) && s.GetBoolean();
            var skipped = root.TryGetProperty("skipped", out var sk) && sk.GetBoolean();
            var message = root.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
            var localPathOut = root.TryGetProperty("localPath", out var lp) ? lp.GetString() : null;
            return new WinScpBridgeResult(success, skipped, message, localPathOut);
        }
        catch (Exception ex)
        {
            return new WinScpBridgeResult(false, false, $"回應解析失敗: {ex.Message}（原始輸出: {lastLine}）", null);
        }
    }
}
