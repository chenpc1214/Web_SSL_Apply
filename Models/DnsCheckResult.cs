namespace SslWebConsole.Models;

public enum DnsStatus
{
    Skip,
    Ok,
    OkWwwWarn,   // OK (www異常)
    OkWww,       // OK-www：主網域不符但 www 吻合，網域欄要改成 www.CommonName
    Fail,
}

public sealed class DnsCheckResult
{
    public required DomainRecord Row { get; init; }
    public DnsStatus Status { get; init; }
    /// <summary>OK-www 狀態下顯示的更正後網域（= www.CommonName），其餘狀態等於原本的 CommonName。</summary>
    public required string EffectiveCommonName { get; init; }
    public string MainIpDisplay { get; init; } = "";
    public string WwwIpDisplay { get; init; } = "";
    public bool MainMatch { get; init; }
    public bool WwwMatch { get; init; }
    public string Remark { get; init; } = "";

    public string StatusLabel => Status switch
    {
        DnsStatus.Ok => "OK",
        DnsStatus.OkWwwWarn => "OK (www異常)",
        DnsStatus.OkWww => "OK-www",
        DnsStatus.Fail => "FAIL",
        _ => "略過",
    };
}
