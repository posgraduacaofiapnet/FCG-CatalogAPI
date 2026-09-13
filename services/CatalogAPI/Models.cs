using MassTransit;
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
    public string UserEmail { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public string Status { get; set; } = OrderStatuses.Pending;
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
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

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
            builder.Property(order => order.UserEmail).IsRequired().HasMaxLength(320);
            builder.Property(order => order.Price).HasColumnType("decimal(18,2)");
            builder.Property(order => order.Status).IsRequired().HasMaxLength(30);
            builder.HasIndex(order => new { order.UserId, order.GameId })
                .IsUnique()
                .HasDatabaseName("IX_Orders_UserId_GameId_Pending")
                .HasFilter("[Status] = 'Pending'");
        });

        modelBuilder.Entity<LibraryItem>(builder =>
        {
            builder.HasKey(item => item.Id);
            builder.Property(item => item.Id).ValueGeneratedNever();
            builder.HasIndex(item => new { item.UserId, item.GameId }).IsUnique();
        });

        modelBuilder.Entity<OutboxMessage>(builder =>
        {
            builder.ToTable("OutboxMessages", tableBuilder =>
            {
                tableBuilder.HasCheckConstraint("CK_OutboxMessages_Attempts", "[Attempts] >= 0");
                tableBuilder.HasCheckConstraint("CK_OutboxMessages_Payload_IsJson", "ISJSON([Payload]) = 1");
                tableBuilder.HasCheckConstraint("CK_OutboxMessages_EventType_NotEmpty", "LEN(LTRIM(RTRIM([EventType]))) > 0");
            });
            builder.HasKey(message => message.Id);
            builder.Property(message => message.Id).ValueGeneratedNever();
            builder.Property(message => message.EventType).IsRequired().HasMaxLength(100);
            builder.Property(message => message.IsSuccessful).HasDefaultValue(false);
            builder.Property(message => message.CreatedAt).IsRequired();
            builder.Property(message => message.Payload).IsRequired().HasColumnType("nvarchar(max)");
            builder.Property(message => message.Attempts).HasDefaultValue(0);
            builder.HasIndex(message => new { message.NextAttemptAt, message.CreatedAt, message.Id })
                .HasDatabaseName("IX_OutboxMessages_Pending_NextAttemptAt_CreatedAt")
                .HasFilter("[IsSuccessful] = 0 AND [Attempts] < 10");
            builder.HasIndex(message => new { message.CreatedAt, message.Id })
                .HasDatabaseName("IX_OutboxMessages_Successful_CreatedAt")
                .HasFilter("[IsSuccessful] = 1");
        });

        modelBuilder.AddInboxStateEntity(entity => entity.ToTable("InboxState", "messaging"));
        modelBuilder.AddOutboxStateEntity(entity => entity.ToTable("OutboxState", "messaging"));
        modelBuilder.AddOutboxMessageEntity(entity => entity.ToTable("OutboxMessage", "messaging"));
    }
}

public sealed record CreateGameRequest(string Title, string Description, decimal Price);
public sealed record UpdateGameRequest(string Title, string Description, decimal Price);
public sealed record PurchaseGameRequest(Guid UserId, Guid GameId);
public sealed record CreateGameReviewRequest(int Rating, string Comment);
public sealed record GameResponse(Guid Id, string Title, string Description, decimal Price);
public sealed record LibraryGameResponse(Guid GameId, string Title, decimal Price, DateTime AcquiredAt);
public sealed record GameReviewResponse(
    Guid Id, Guid GameId, Guid UserId, int Rating, string Comment, DateTime CreatedAt);

/// <summary>Parâmetros de paginação reutilizáveis.</summary>
public sealed record PaginationParameters(int Page, int PageSize)
{
    public const int DefaultPage = 1;
    public const int DefaultPageSize = 10;
    public const int MaxPageSize = 100;

    public int Skip => (Page - 1) * PageSize;

    public static PaginationParameters From(int? page, int? pageSize)
    {
        var p = Math.Max(1, page ?? DefaultPage);
        var ps = Math.Clamp(pageSize ?? DefaultPageSize, 1, MaxPageSize);
        return new PaginationParameters(p, ps);
    }
}

/// <summary>Resultado paginado idêntico ao da Fase 1.</summary>
public sealed record PagedResult<T>(
    IReadOnlyList<T> Items,
    int Page,
    int PageSize,
    int TotalCount)
{
    public int TotalPages => TotalCount == 0
        ? 0
        : (int)Math.Ceiling(TotalCount / (double)PageSize);

    public bool HasPreviousPage => Page > 1;
    public bool HasNextPage => Page < TotalPages;
}
