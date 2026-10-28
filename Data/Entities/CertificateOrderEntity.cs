namespace SslWebConsole.Data.Entities;

public sealed class CertificateOrderEntity
{
    public int Id { get; set; }
    public int DomainId { get; set; }
    public string? CertId { get; set; }
    public string Status { get; set; } = "Pending"; // Pending / Issued / Failed / Expired
    public DateTime PlacedAt { get; set; }
    public DateTime? IssuedAt { get; set; }
    public DateTime? CertExpiresAt { get; set; }
}
