using Microsoft.EntityFrameworkCore;

namespace TicketRush.Catalog.Storage;

public sealed class CatalogDbContext(DbContextOptions<CatalogDbContext> options) : DbContext(options)
{
    public DbSet<CatalogEvent> Events => Set<CatalogEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<CatalogEvent>(builder =>
        {
            builder.ToTable("events");
            builder.HasKey(x => x.Id);
            builder.Property(x => x.Id).HasColumnName("id");
            builder.Property(x => x.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
            builder.Property(x => x.StartsAtUtc).HasColumnName("starts_at_utc");
        });
    }
}
