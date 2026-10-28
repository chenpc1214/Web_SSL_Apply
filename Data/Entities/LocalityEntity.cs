namespace SslWebConsole.Data.Entities;

public sealed class LocalityEntity
{
    public int Id { get; set; }
    /// <summary>存進 Domains.Locality / CSR 的 ST、L 欄位的值——英文，CA 端只接受這個。</summary>
    public string Name { get; set; } = "";
    /// <summary>下拉選單顯示用的中文名稱；存檔時仍然存 Name（英文）。</summary>
    public string? DisplayNameZh { get; set; }
    public int? SortOrder { get; set; }
}
