using Microsoft.EntityFrameworkCore;
using SslWebConsole.Data.Entities;

namespace SslWebConsole.Data;

public sealed class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<DomainEntity> Domains => Set<DomainEntity>();
    public DbSet<CertificateOrderEntity> CertificateOrders => Set<CertificateOrderEntity>();
    public DbSet<CertificateFileEntity> CertificateFiles => Set<CertificateFileEntity>();
    public DbSet<SettingsEntity> Settings => Set<SettingsEntity>();
    public DbSet<LocalityEntity> Localities => Set<LocalityEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DomainEntity>(e =>
        {
            e.HasIndex(x => x.CommonName).IsUnique();
        });

        modelBuilder.Entity<CertificateOrderEntity>(e =>
        {
            e.HasIndex(x => x.DomainId);
        });

        modelBuilder.Entity<CertificateFileEntity>(e =>
        {
            e.HasIndex(x => x.DomainId);
        });

        modelBuilder.Entity<LocalityEntity>(e =>
        {
            e.HasIndex(x => x.Name).IsUnique();
        });
    }
}
