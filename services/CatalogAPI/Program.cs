using System.Security.Claims;
using System.Text;
using CatalogAPI;
using FluentValidation;
using MassTransit;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Serilog;
using Serilog.Formatting.Compact;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Service", "CatalogAPI")
    .WriteTo.Console(new RenderedCompactJsonFormatter()));

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddDbContext<CatalogDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));
builder.Services.AddScoped<CatalogService>();
builder.Services.AddScoped<CorrelationContext>();
builder.Services.AddScoped<ICatalogEventPublisher, MassTransitCatalogEventPublisher>();
builder.Services.AddScoped<IValidator<CreateGameRequest>, CreateGameRequestValidator>();
builder.Services.AddScoped<IValidator<UpdateGameRequest>, UpdateGameRequestValidator>();
builder.Services.AddScoped<IValidator<PurchaseGameRequest>, PurchaseGameRequestValidator>();

var jwtKey = builder.Configuration["Jwt:Key"] ?? throw new InvalidOperationException("Jwt:Key is required.");
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.ASCII.GetBytes(jwtKey)),
            ValidateIssuer = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"] ?? "UsersAPI",
            ValidateAudience = true,
            ValidAudience = builder.Configuration["Jwt:Audience"] ?? "FCG"
        };
    });
builder.Services.AddAuthorization();

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

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseSerilogRequestLogging();
app.UseSwagger();
app.UseSwaggerUI();
app.UseAuthentication();
app.UseAuthorization();

static bool IsOwner(ClaimsPrincipal user, Guid userId)
{
    var claim = user.FindFirstValue("user_id");
    return Guid.TryParse(claim, out var callerId) && callerId == userId;
}

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
}).RequireAuthorization();

app.MapPut("/api/games/{id:guid}", async (
    Guid id,
    UpdateGameRequest request,
    IValidator<UpdateGameRequest> validator,
    CatalogService service,
    CancellationToken cancellationToken) =>
{
    var validation = await validator.ValidateAsync(request, cancellationToken);
    if (!validation.IsValid)
    {
        return Results.ValidationProblem(validation.ToDictionary());
    }

    var game = await service.UpdateGameAsync(id, request, cancellationToken);
    return game is null ? Results.NotFound(new { error = "Games.NotFound" }) : Results.Ok(game);
}).RequireAuthorization();

app.MapDelete("/api/games/{id:guid}", async (Guid id, CatalogService service, CancellationToken cancellationToken) =>
{
    var deleted = await service.DeleteGameAsync(id, cancellationToken);
    return deleted ? Results.NoContent() : Results.NotFound(new { error = "Games.NotFound" });
}).RequireAuthorization();

app.MapPost("/api/library/purchase", async (
    PurchaseGameRequest request,
    IValidator<PurchaseGameRequest> validator,
    ClaimsPrincipal user,
    CatalogService service,
    CancellationToken cancellationToken) =>
{
    var validation = await validator.ValidateAsync(request, cancellationToken);
    if (!validation.IsValid)
    {
        return Results.ValidationProblem(validation.ToDictionary());
    }

    if (!IsOwner(user, request.UserId))
    {
        return Results.Forbid();
    }

    return await service.PurchaseAsync(request, cancellationToken);
}).RequireAuthorization();

app.MapGet("/api/library/{userId:guid}", async (Guid userId, ClaimsPrincipal user, CatalogService service, CancellationToken cancellationToken) =>
{
    if (!IsOwner(user, userId))
    {
        return Results.Forbid();
    }

    return Results.Ok(await service.GetLibraryAsync(userId, cancellationToken));
}).RequireAuthorization();

app.Run();

public partial class Program;
