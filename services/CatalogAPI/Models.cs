using Microsoft.EntityFrameworkCore;

namespace CatalogAPI;

public sealed class Game
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class PurchaseOrder
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public Guid GameId { get; set; }
    public string GameTitle { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public string Status { get; set; } = "Pending";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class LibraryItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public Guid GameId { get; set; }
    public DateTime AcquiredAt { get; set; } = DateTime.UtcNow;
}

public sealed class CatalogDbContext(DbContextOptions<CatalogDbContext> options) : DbContext(options)
{
    public DbSet<Game> Games => Set<Game>();
    public DbSet<PurchaseOrder> Orders => Set<PurchaseOrder>();
    public DbSet<LibraryItem> LibraryItems => Set<LibraryItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Game>(builder =>
        {
            builder.HasKey(game => game.Id);
            builder.Property(game => game.Id).ValueGeneratedNever();
            builder.Property(game => game.Title).IsRequired().HasMaxLength(150);
            builder.Property(game => game.Description).IsRequired().HasMaxLength(500);
            builder.Property(game => game.Price).HasColumnType("decimal(18,2)");
        });

        modelBuilder.Entity<PurchaseOrder>(builder =>
        {
            builder.HasKey(order => order.Id);
            builder.Property(order => order.Id).ValueGeneratedNever();
            builder.Property(order => order.GameTitle).IsRequired().HasMaxLength(150);
            builder.Property(order => order.Price).HasColumnType("decimal(18,2)");
            builder.Property(order => order.Status).IsRequired().HasMaxLength(30);
        });

        modelBuilder.Entity<LibraryItem>(builder =>
        {
            builder.HasKey(item => item.Id);
            builder.Property(item => item.Id).ValueGeneratedNever();
            builder.HasIndex(item => new { item.UserId, item.GameId }).IsUnique();
        });
    }
}

public sealed record CreateGameRequest(string Title, string Description, decimal Price);
public sealed record PurchaseGameRequest(Guid UserId, Guid GameId);
public sealed record GameResponse(Guid Id, string Title, string Description, decimal Price);
public sealed record LibraryGameResponse(Guid GameId, string Title, decimal Price, DateTime AcquiredAt);
