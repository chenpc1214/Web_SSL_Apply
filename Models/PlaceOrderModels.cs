using System.Text.Json.Serialization;

namespace SslWebConsole.Models;

public sealed record OrderSpecResult(string? ProductCode, int? Years, string? Error)
{
    public bool IsValid => Error is null;
}

public sealed record ExistingOrderInfo(string? CertId, string? Status, DateTime? EndDate, int? DaysLeft);

public sealed record OrderPlacementResult(string CommonName, bool Success, bool Skipped, string Message, string? CertId = null);

public sealed class DomainInfoEntry
{
    [JsonPropertyName("domainName")] public string DomainName { get; set; } = "";
    [JsonPropertyName("dcvMethod")] public string DcvMethod { get; set; } = "";
}

/// <summary>對應 4_Place_Order.ps1 組出來、序列化後送進 /ssl/validate 與 /ssl/place 的 "params" 欄位。</summary>
public sealed class PlaceOrderParams
{
    [JsonPropertyName("server")] public string Server { get; set; } = "other";
    [JsonPropertyName("csr")] public string Csr { get; set; } = "";
    [JsonPropertyName("dcvMethod")] public string DcvMethod { get; set; } = "";
    [JsonPropertyName("domainInfo")] public List<DomainInfoEntry> DomainInfo { get; set; } = [];
    [JsonPropertyName("Administrator")] public SslWebConsole.Services.AdministratorInfo Administrator { get; set; } = new();
}
