using Microsoft.EntityFrameworkCore;
using Shortener.Api.Domain;

namespace Shortener.Api.Persistence;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<ShortLink> Links => Set<ShortLink>();
    public DbSet<ClickEvent> Clicks => Set<ClickEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ShortLink>(entity =>
        {
            entity.HasKey(link => link.Id);
            entity.HasIndex(link => link.Code).IsUnique();
            entity.Property(link => link.Code).HasMaxLength(32);
            entity.Property(link => link.TargetUrl).HasMaxLength(2048);
        });

        modelBuilder.Entity<ClickEvent>(entity =>
        {
            entity.HasKey(click => click.Id);
            entity.HasIndex(click => new { click.Code, click.OccurredAtUtc });
            entity.Property(click => click.Code).HasMaxLength(32);
            entity.Property(click => click.ReferrerHost).HasMaxLength(253);
            entity.Property(click => click.CountryCode).HasMaxLength(2);
        });
    }
}