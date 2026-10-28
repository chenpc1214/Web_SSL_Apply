using CsvHelper.Configuration.Attributes;

namespace SslWebConsole.Models;

/// <summary>對應 Certificates_List.csv 的一列（7_Find_Cert_Files.ps1 輸出 / 8_Create_PFX.ps1 輸入）。</summary>
public sealed class CertFileGroup
{
    [Name("Domain")]
    public string Domain { get; set; } = "";

    [Name("CertPath")]
    public string CertPath { get; set; } = "";

    [Name("KeyPath")]
    public string KeyPath { get; set; } = "";

    [Name("BundlePath")]
    public string? BundlePath { get; set; }
}
