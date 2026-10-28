namespace SslWebConsole.Data.Entities;

/// <summary>單一列（Id 固定 = 1），對應 nicsrs_config.json 的內容。</summary>
public sealed class SettingsEntity
{
    public int Id { get; set; } = 1;
    public string ApiBase { get; set; } = "";
    public string ApiTokenEncrypted { get; set; } = "";
    public string WorkRoot { get; set; } = "";
    /// <summary>步驟6「封存並清理」把完成的網域資料夾搬去的地方。選填，空白時那個按鈕不能按。
    /// 既有資料庫沒有這個欄位，由 Program.cs 啟動時補上（EnsureCreated 不會改既有表結構）。</summary>
    public string ArchiveRoot { get; set; } = "";
    /// <summary>人工模式（其他憑證商）的工作區，選填，空白時總覽的「其他憑證商」不能按。
    /// 既有資料庫沒有這個欄位，由 Program.cs 啟動時補上。</summary>
    public string ManualWorkRoot { get; set; } = "";
    public string DefaultDcvMethod { get; set; } = "HTTP_CSR_HASH";
    public int PollIntervalSeconds { get; set; } = 120;
    public int PollMaxMinutes { get; set; } = 60;
    public int PageSize { get; set; } = 20;

    public string AdminFirstName { get; set; } = "";
    public string AdminLastName { get; set; } = "";
    public string AdminJob { get; set; } = "";
    public string AdminOrganization { get; set; } = "";
    public string AdminEmail { get; set; } = "";
    public string AdminMobile { get; set; } = "";
    public string AdminCountry { get; set; } = "";
    public string AdminState { get; set; } = "";
    public string AdminCity { get; set; } = "";
    public string AdminPostCode { get; set; } = "";
    public string AdminAddress { get; set; } = "";

    /// <summary>CRM 校對用（CRM_Verify）。Session cookie，會過期，跟 nicsrs_config.json 無關，不外export。</summary>
    public string CrmCookieEncrypted { get; set; } = "";
    public string CrmBaseUrl { get; set; } = "https://work.eztrust.tw";

    public DateTime UpdatedAt { get; set; }
}
