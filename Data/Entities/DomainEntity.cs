namespace SslWebConsole.Data.Entities;

public sealed class DomainEntity
{
    public int Id { get; set; }
    /// <summary>對應 SSL_List.csv 的 "ID" 欄位——使用者自己的備註編號（客戶/訂單編號），不是 DB 主鍵，腳本不讀。</summary>
    public string ExternalId { get; set; } = "";
    public string CommonName { get; set; } = "";
    public string Organization { get; set; } = "";
    public string Ip { get; set; } = "";
    public string FtpUser { get; set; } = "";
    public string FtpPassEncrypted { get; set; } = "";
    public string FtpFolder { get; set; } = "";
    public string ProductCode { get; set; } = "";
    public string Years { get; set; } = "";
    public string Locality { get; set; } = "";
    public string Remark { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    /// <summary>網域已經是已完成狀態，但正在準備續約/重簽（存過新資料、還沒真的送出步驟3的
    /// 動作）——特意存資料庫、不是放記憶體，這樣網站程式重啟也不會忘記半路做到哪。</summary>
    public bool PendingRenewal { get; set; }
    /// <summary>步驟5「暫緩」的標記時間，null＝沒有暫緩。用在客戶那邊卡住、短期內不會簽發的網域
    /// （例如 Cloudflare 擋住檔案驗證）：步驟5開頁預設不勾選、輪詢跳過；步驟6封存一律略過。
    /// 既有資料庫沒有這個欄位，由 Program.cs 啟動時補上。</summary>
    public DateTime? OnHoldSince { get; set; }
}
