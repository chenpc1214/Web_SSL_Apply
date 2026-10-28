using CsvHelper.Configuration.Attributes;

namespace SslWebConsole.Models;

/// <summary>對應 SSL_List.csv 的一列，欄位順序/名稱與現有腳本完全一致。</summary>
public sealed class DomainRecord
{
    [Name("ID")]
    public string Id { get; set; } = "";

    [Name("CommonName")]
    public string CommonName { get; set; } = "";

    [Name("Organization")]
    public string Organization { get; set; } = "";

    // Ip/FtpUser/FtpPass/FtpFolder 進 set 就自動 Trim：這幾個欄位會直接組成 FTP 連線
    // 參數，畫面輸入或複製貼上常常不小心帶進頭尾空白／換行，一個空白就會讓 WinSCP
    // 把整串（含空白）當主機名稱去連，連線直接失敗且訊息不會明講是空白造成的。
    private string _ip = "";
    [Name("IP")]
    public string Ip { get => _ip; set => _ip = value?.Trim() ?? ""; }

    private string _ftpUser = "";
    [Name("FtpUser")]
    public string FtpUser { get => _ftpUser; set => _ftpUser = value?.Trim() ?? ""; }

    private string _ftpPass = "";
    [Name("FtpPass")]
    public string FtpPass { get => _ftpPass; set => _ftpPass = value?.Trim() ?? ""; }

    private string _ftpFolder = "";
    [Name("FtpFolder")]
    public string FtpFolder { get => _ftpFolder; set => _ftpFolder = value?.Trim() ?? ""; }

    [Name("ProductCode")]
    public string ProductCode { get; set; } = "";

    [Name("Years")]
    public string Years { get; set; } = "";

    [Name("Locality")]
    public string Locality { get; set; } = "";

    [Name("Remark")]
    public string Remark { get; set; } = "";
}
