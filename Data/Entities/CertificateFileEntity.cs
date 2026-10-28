namespace SslWebConsole.Data.Entities;

public sealed class CertificateFileEntity
{
    public int Id { get; set; }
    public int DomainId { get; set; }
    public DateTime GeneratedAt { get; set; }
}
