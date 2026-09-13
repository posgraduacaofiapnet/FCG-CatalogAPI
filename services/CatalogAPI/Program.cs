using System.Security.Claims;
using System.Text;
using CatalogAPI;
using FluentValidation;
using MassTransit;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using MongoDB.Driver;
using Prometheus;
using Serilog;
using Serilog.Formatting.Compact;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Service", "CatalogAPI")
    .WriteTo.Console(new RenderedCompactJsonFormatter()));

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new() { Title = "FCG Catalog API", Version = "v1" });
    
    options.AddSecurityDefinition("Bearer", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.ApiKey,
        Scheme = "Bearer",
        BearerFormat = "JWT",
        In = Microsoft.OpenApi.Models.ParameterLocation.Header,
        Description = "Insira o token JWT desta forma: Bearer {seu_token}"
    });

    options.AddSecurityRequirement(new Microsoft.OpenApi.Models.OpenApiSecurityRequirement
    {
        {
            new Microsoft.OpenApi.Models.OpenApiSecurityScheme
            {
                Reference = new Microsoft.OpenApi.Models.OpenApiReference
                {
                    Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            Array.Empty<string>()
        }
    });
});
builder.Services.AddDbContext<CatalogDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

var redisConnection = builder.Configuration.GetConnectionString("Redis")
    ?? builder.Configuration["Redis:Configuration"];
if (string.IsNullOrWhiteSpace(redisConnection))
{
    builder.Services.AddDistributedMemoryCache();
}
else
{
    builder.Services.AddStackExchangeRedisCache(options =>
    {
        options.Configuration = redisConnection;
        options.InstanceName = "fcg:";
    });
}

builder.Services.AddSingleton<IGameCatalogCache, DistributedGameCatalogCache>();

var mongoConnection = builder.Configuration.GetConnectionString("MongoDB")
    ?? throw new InvalidOperationException("ConnectionStrings:MongoDB is required.");
var mongoDatabaseName = builder.Configuration["Mongo:Database"] ?? "fcg_catalog";
builder.Services.AddSingleton<IMongoClient>(_ => new MongoClient(mongoConnection));
builder.Services.AddSingleton(sp => sp.GetRequiredService<IMongoClient>().GetDatabase(mongoDatabaseName));
builder.Services.AddSingleton<IGameReviewStore, MongoGameReviewStore>();
builder.Services.AddScoped<GameReviewService>();

builder.Services.AddScoped<CatalogService>();
builder.Services.AddScoped<CorrelationContext>();
builder.Services.AddScoped<ICatalogEventPublisher, MassTransitCatalogEventPublisher>();
builder.Services.AddScoped<IValidator<CreateGameRequest>, CreateGameRequestValidator>();
builder.Services.AddScoped<IValidator<UpdateGameRequest>, UpdateGameRequestValidator>();
builder.Services.AddScoped<IValidator<PurchaseGameRequest>, PurchaseGameRequestValidator>();
builder.Services.AddScoped<IValidator<CreateGameReviewRequest>, CreateGameReviewRequestValidator>();

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
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddProblemDetails();

builder.Services.AddMassTransit(bus =>
{
    bus.AddConsumer<PaymentProcessedConsumer>();
    bus.AddEntityFrameworkOutbox<CatalogDbContext>(outbox =>
    {
        outbox.QueryDelay = TimeSpan.FromSeconds(1);
        outbox.UseSqlServer();
        outbox.UseBusOutbox();
    });

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

app.UseExceptionHandler();
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseSerilogRequestLogging();
app.UseHttpMetrics();
app.UseSwagger();
app.UseSwaggerUI();
app.UseAuthentication();
app.UseAuthorization();

static bool IsOwner(ClaimsPrincipal user, Guid userId)
{
    var claim = user.FindFirstValue("user_id");
    return Guid.TryParse(claim, out var callerId) && callerId == userId;
}

static string? GetUserEmail(ClaimsPrincipal user) =>
    user.FindFirstValue(ClaimTypes.Email) ?? user.FindFirstValue("email");

app.MapGet("/health", () => Results.Ok(new { status = "Healthy", service = "CatalogAPI" }));
app.MapMetrics();

app.MapGet("/api/games", async (
    CatalogService service,
    CancellationToken cancellationToken,
    int? page,
    int? pageSize) =>
{
    var pagination = PaginationParameters.From(page, pageSize);
    return Results.Ok(await service.GetGamesAsync(pagination, cancellationToken));
});

app.MapGet("/api/games/{id:guid}", async (Guid id, CatalogService service, CancellationToken cancellationToken) =>
{
    var game = await service.GetGameAsync(id, cancellationToken);
    return game is null ? Results.NotFound(new { error = "Games.NotFound" }) : Results.Ok(game);
});

app.MapGet("/api/games/{id:guid}/reviews", async (
    Guid id,
    GameReviewService reviews,
    CancellationToken cancellationToken) =>
{
    var list = await reviews.GetByGameIdAsync(id, cancellationToken);
    return list is null ? Results.NotFound(new { error = "Games.NotFound" }) : Results.Ok(list);
});

app.MapPost("/api/games/{id:guid}/reviews", async (
    Guid id,
    CreateGameReviewRequest request,
    IValidator<CreateGameReviewRequest> validator,
    ClaimsPrincipal user,
    GameReviewService reviews,
    CancellationToken cancellationToken) =>
{
    var validation = await validator.ValidateAsync(request, cancellationToken);
    if (!validation.IsValid)
    {
        return Results.ValidationProblem(validation.ToDictionary());
    }

    var claim = user.FindFirstValue("user_id");
    if (!Guid.TryParse(claim, out var userId))
    {
        return Results.Unauthorized();
    }

    var created = await reviews.CreateAsync(id, userId, request, cancellationToken);
    return created is null
        ? Results.NotFound(new { error = "Games.NotFound" })
        : Results.Created($"/api/games/{id}/reviews/{created.Id}", created);
}).RequireAuthorization();

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
}).RequireAuthorization(policy => policy.RequireRole("Admin"));

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
}).RequireAuthorization(policy => policy.RequireRole("Admin"));

app.MapDelete("/api/games/{id:guid}", async (Guid id, CatalogService service, CancellationToken cancellationToken) =>
{
    var deleted = await service.DeleteGameAsync(id, cancellationToken);
    return deleted ? Results.NoContent() : Results.NotFound(new { error = "Games.NotFound" });
}).RequireAuthorization(policy => policy.RequireRole("Admin"));

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

    var userEmail = GetUserEmail(user);
    if (string.IsNullOrWhiteSpace(userEmail))
    {
        return Results.Unauthorized();
    }

    return await service.PurchaseAsync(request, userEmail, cancellationToken);
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
