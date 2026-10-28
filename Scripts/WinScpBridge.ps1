# WinScpBridge.ps1
# 網頁程式（SslWebConsole）用來操作 WinSCP 的橋接腳本。
#
# 背景：WinSCPnet.dll 在 .NET 10 行程裡直接呼叫 Session.Open() 時，會讓它另外
# 產生的 WinSCP.com 子行程原生當機（0xC0000005 access violation），研判是 .NET 10
# 產生子行程時跟這支舊版 WinSCP.com 溝通用的匿名管線方式對不上。改用 PowerShell（跟
# 1_Check_DNS.ps1 / 2_Upload_Validation.ps1 / 5_Patch_WebConfig.ps1 同一套、已證實
# 穩定可用的 Windows PowerShell 5.1 執行環境）當橋接層即可繞開。
#
# 協定：從 stdin 讀一行 JSON 請求，處理完後往 stdout 印一行 JSON 回應。
# 密碼故意不透過命令列參數傳遞（工作管理員看得到命令列），改用 stdin。
[CmdletBinding()]
param(
    [string]$WinScpDllPath = "C:\Program Files (x86)\WinSCP\WinSCPnet.dll"
)

$ErrorActionPreference = "Stop"

# Windows PowerShell 5.1 主控台預設用系統內碼（例如 Big5）讀寫，中文訊息經過
# stdin/stdout 重新導向會亂碼甚至遺失字元。[Console]::InputEncoding 在 stdin 被
# 重新導向（管線）時無法設定會直接丟例外，所以改成直接包裝底層 Stream，跟
# C# 那端 ProcessStartInfo.StandardInputEncoding/StandardOutputEncoding=UTF8 對齊。
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$stdinReader = New-Object System.IO.StreamReader([Console]::OpenStandardInput(), $utf8NoBom)
$stdoutWriter = New-Object System.IO.StreamWriter([Console]::OpenStandardOutput(), $utf8NoBom)
$stdoutWriter.AutoFlush = $true

function Write-JsonResult {
    param($Obj)
    $stdoutWriter.WriteLine(($Obj | ConvertTo-Json -Compress))
}

try {
    Add-Type -Path $WinScpDllPath
}
catch {
    Write-JsonResult @{ success = $false; message = "無法載入 WinSCPnet.dll ($WinScpDllPath): $($_.Exception.Message)" }
    exit 0
}

$reqLine = $stdinReader.ReadLine()
try {
    $req = $reqLine | ConvertFrom-Json
}
catch {
    Write-JsonResult @{ success = $false; message = "無法解析請求 JSON: $($_.Exception.Message)" }
    exit 0
}

function Open-BridgeFtpSession {
    param([string]$Ip, [string]$User, [string]$Pass, [int]$TimeoutSec)

    $options = New-Object WinSCP.SessionOptions
    $options.Protocol = [WinSCP.Protocol]::Ftp
    $options.HostName = $Ip
    $options.UserName = $User
    $options.Password = $Pass
    $options.FtpSecure = [WinSCP.FtpSecure]::Explicit
    $options.GiveUpSecurityAndAcceptAnyTlsHostCertificate = $true
    $options.Timeout = New-Object TimeSpan(0, 0, $TimeoutSec)

    $session = New-Object WinSCP.Session
    try {
        $session.Open($options)
        return $session
    }
    catch {
        $options.FtpSecure = [WinSCP.FtpSecure]::None
        $options.GiveUpSecurityAndAcceptAnyTlsHostCertificate = $false
        try {
            $session.Open($options)
            return $session
        }
        catch {
            $session.Dispose()
            throw
        }
    }
}

$timeoutSec = if ($req.TimeoutSeconds) { [int]$req.TimeoutSeconds } else { 15 }

try {
    $session = Open-BridgeFtpSession -Ip $req.Ip -User $req.User -Pass $req.Password -TimeoutSec $timeoutSec
}
catch {
    Write-JsonResult @{ success = $false; message = "無法連線至 FTP ($($req.Ip)): $($_.Exception.Message)" }
    exit 0
}

try {
    switch ($req.Action) {
        "test" {
            if ($session.FileExists($req.RemoteDir)) {
                Write-JsonResult @{ success = $true; message = "連線OK，資料夾OK ($($req.RemoteDir))" }
            }
            else {
                Write-JsonResult @{ success = $false; message = "連線OK，但找不到遠端路徑: $($req.RemoteDir)" }
            }
        }
        "upload-validation" {
            $wellKnown = $req.RemoteDir.TrimEnd('/') + "/.well-known/"
            $pkiValidation = $wellKnown + "pki-validation/"
            if (-not $session.FileExists($wellKnown)) { $session.CreateDirectory($wellKnown) }
            if (-not $session.FileExists($pkiValidation)) { $session.CreateDirectory($pkiValidation) }

            $transferOptions = New-Object WinSCP.TransferOptions
            $transferOptions.TransferMode = [WinSCP.TransferMode]::Binary
            $result = $session.PutFiles($req.LocalPath, $pkiValidation, $false, $transferOptions)
            $result.Check()
            Write-JsonResult @{ success = $true; message = "上傳完成 ($($req.Ip))" }
        }
        "get" {
            if (-not $session.FileExists($req.RemotePath)) {
                Write-JsonResult @{ success = $false; skipped = $true; message = "遠端不存在 $($req.RemotePath)" }
            }
            else {
                $transferOptions = New-Object WinSCP.TransferOptions
                $transferOptions.TransferMode = [WinSCP.TransferMode]::Binary
                $result = $session.GetFiles($req.RemotePath, $req.LocalPath, $false, $transferOptions)
                $result.Check()
                Write-JsonResult @{ success = $true; message = "下載完成"; localPath = $req.LocalPath }
            }
        }
        "put" {
            $transferOptions = New-Object WinSCP.TransferOptions
            $transferOptions.TransferMode = [WinSCP.TransferMode]::Binary
            $result = $session.PutFiles($req.LocalPath, $req.RemotePath, $false, $transferOptions)
            $result.Check()
            Write-JsonResult @{ success = $true; message = "上傳完成" }
        }
        default {
            Write-JsonResult @{ success = $false; message = "未知的 Action: $($req.Action)" }
        }
    }
}
catch {
    Write-JsonResult @{ success = $false; message = "處理錯誤: $($_.Exception.Message)" }
}
finally {
    $session.Dispose()
}
