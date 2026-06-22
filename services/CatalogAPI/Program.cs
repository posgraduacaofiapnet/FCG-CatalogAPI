using CatalogAPI;
using FluentValidation;
using MassTransit;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddDbContext<CatalogDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));
builder.Services.AddScoped<CatalogService>();
builder.Services.AddScoped<ICatalogEventPublisher, MassTransitCatalogEventPublisher>();
builder.Services.AddScoped<IValidator<CreateGameRequest>, CreateGameRequestValidator>();
builder.Services.AddScoped<IValidator<PurchaseGameRequest>, PurchaseGameRequestValidator>();

builder.Services.AddMassTransit(bus =>
{
    bus.AddConsumer<PaymentProcessedConsumer>();

    bus.UsingRabbitMq((context, cfg) =>
    {
        cfg.Host(builder.Configuration["RabbitMq:Host"] ?? "localhost", "/", host =>
        {
            host.Username(builder.Configuration["RabbitMq:Username"] ?? "guest");
            host.Password(builder.Configuration["RabbitMq:Password"] ?? "guest");
        });
        cfg.ReceiveEndpoint(builder.Configuration["RabbitMq:PaymentProcessedQueue"] ?? "catalog-payment-processed", endpoint =>
        {
            endpoint.ConfigureConsumer<PaymentProcessedConsumer>(context);
        });
    });
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
    await dbContext.Database.EnsureCreatedAsync();
}

app.UseSwagger();
app.UseSwaggerUI();

app.MapGet("/health", () => Results.Ok(new { status = "Healthy", service = "CatalogAPI" }));

app.MapGet("/api/games", async (CatalogService service, CancellationToken cancellationToken) =>
    Results.Ok(await service.GetGamesAsync(cancellationToken)));

app.MapGet("/api/games/{id:guid}", async (Guid id, CatalogService service, CancellationToken cancellationToken) =>
{
    var game = await service.GetGameAsync(id, cancellationToken);
    return game is null ? Results.NotFound(new { error = "Games.NotFound" }) : Results.Ok(game);
});

app.MapPost("/api/games", async (
    CreateGameRequest request,
    IValidator<CreateGameRequest> validator,
    CatalogService service,
    CancellationToken cancellationToken) =>
{
    var validation = await validator.ValidateAsync(request, cancellationToken);
    if (!validation.IsValid)
    {
        return Results.ValidationProblem(validation.ToDictionary());
    }

    var game = await service.CreateGameAsync(request, cancellationToken);
    return Results.Created($"/api/games/{game.Id}", game);
});

app.MapPost("/api/library/purchase", async (
    PurchaseGameRequest request,
    IValidator<PurchaseGameRequest> validator,
    CatalogService service,
    CancellationToken cancellationToken) =>
{
    var validation = await validator.ValidateAsync(request, cancellationToken);
    return validation.IsValid
        ? await service.PurchaseAsync(request, cancellationToken)
        : Results.ValidationProblem(validation.ToDictionary());
});

app.MapGet("/api/library/{userId:guid}", async (Guid userId, CatalogService service, CancellationToken cancellationToken) =>
    Results.Ok(await service.GetLibraryAsync(userId, cancellationToken)));

app.Run();

public partial class Program;
