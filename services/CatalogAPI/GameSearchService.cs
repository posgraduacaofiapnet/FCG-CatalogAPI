using Elastic.Clients.Elasticsearch;
using Elastic.Clients.Elasticsearch.Core.Search;
using Elastic.Clients.Elasticsearch.QueryDsl;

namespace CatalogAPI;

public sealed class GameSearchDocument
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public sealed record GameSearchResult(
    Guid Id,
    string Title,
    string Description,
    decimal Price,
    double Score);

public sealed record ReindexGamesResponse(int IndexedCount);

public interface IGameSearchService
{
    Task EnsureIndexAsync(CancellationToken cancellationToken);
    Task IndexGameAsync(Game game, CancellationToken cancellationToken);
    Task RemoveGameAsync(Guid gameId, CancellationToken cancellationToken);
    Task<IReadOnlyList<GameSearchResult>> SearchAsync(string query, int limit, CancellationToken cancellationToken);
}

public sealed class ElasticsearchGameSearchService(
    ElasticsearchClient client,
    IConfiguration configuration,
    ILogger<ElasticsearchGameSearchService> logger) : IGameSearchService
{
    public const string DefaultIndexName = "fcg-games";
    public const int DefaultLimit = 20;
    public const int MaxLimit = 100;

    private readonly string _indexName = configuration["Elasticsearch:Index"] ?? DefaultIndexName;

    public async Task EnsureIndexAsync(CancellationToken cancellationToken)
    {
        try
        {
            var existsResponse = await client.Indices.ExistsAsync(_indexName, cancellationToken);
            if (existsResponse.Exists)
            {
                return;
            }

            var createResponse = await client.Indices.CreateAsync(_indexName, create => create
                .Mappings(mappings => mappings
                    .Properties<GameSearchDocument>(props => props
                        .Keyword(doc => doc.Id)
                        .Text(doc => doc.Title, text => text.Analyzer("brazilian"))
                        .Text(doc => doc.Description, text => text.Analyzer("brazilian"))
                        .DoubleNumber(doc => doc.Price)
                        .Boolean(doc => doc.IsActive)
                        .Date(doc => doc.UpdatedAt))),
                cancellationToken);

            if (!createResponse.IsValidResponse)
            {
                logger.LogWarning(
                    "Nao foi possivel criar indice '{IndexName}' antecipadamente no Elasticsearch: {DebugInformation}",
                    _indexName,
                    createResponse.DebugInformation);
            }
            else
            {
                logger.LogInformation("Indice Elasticsearch '{IndexName}' criado com mapeamento otimizado (analyzer=brazilian).", _indexName);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Erro ao verificar/criar indice '{IndexName}' no Elasticsearch.", _indexName);
        }
    }

    public async Task IndexGameAsync(Game game, CancellationToken cancellationToken)
    {
        try
        {
            var document = new GameSearchDocument
            {
                Id = game.Id,
                Title = game.Title,
                Description = game.Description,
                Price = game.Price,
                IsActive = game.IsActive,
                UpdatedAt = DateTime.UtcNow
            };

            var response = await client.IndexAsync(document, idx => idx
                .Index(_indexName)
                .Id(game.Id.ToString())
                .Refresh(Refresh.True), cancellationToken);

            if (!response.IsValidResponse)
            {
                logger.LogWarning(
                    "Falha ao indexar jogo {GameId} no Elasticsearch: {DebugInformation}",
                    game.Id,
                    response.DebugInformation);
            }
            else
            {
                logger.LogInformation("Jogo {GameId} sincronizado no indice Elasticsearch '{IndexName}'.", game.Id, _indexName);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Erro ao sincronizar jogo {GameId} no Elasticsearch.", game.Id);
        }
    }

    public async Task RemoveGameAsync(Guid gameId, CancellationToken cancellationToken)
    {
        try
        {
            var response = await client.DeleteAsync<GameSearchDocument>(
                gameId.ToString(),
                del => del.Index(_indexName).Refresh(Refresh.True),
                cancellationToken);

            if (!response.IsValidResponse && response.Result != Result.NotFound)
            {
                logger.LogWarning(
                    "Falha ao remover jogo {GameId} do Elasticsearch: {DebugInformation}",
                    gameId,
                    response.DebugInformation);
            }
            else
            {
                logger.LogInformation("Jogo {GameId} removido do indice Elasticsearch '{IndexName}'.", gameId, _indexName);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Erro ao remover jogo {GameId} do Elasticsearch.", gameId);
        }
    }

    public async Task<IReadOnlyList<GameSearchResult>> SearchAsync(
        string query,
        int limit,
        CancellationToken cancellationToken)
    {
        var normalizedQuery = query?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedQuery))
        {
            return [];
        }

        var clampedLimit = Math.Clamp(limit <= 0 ? DefaultLimit : limit, 1, MaxLimit);

        var response = await client.SearchAsync<GameSearchDocument>(search => search
            .Index(_indexName)
            .Size(clampedLimit)
            .Query(q => q
                .Bool(b => b
                    .Must(m => m
                        .MultiMatch(mm => mm
                            .Query(normalizedQuery)
                            .Fields(Infer.Field<GameSearchDocument>(doc => doc.Title, 3.0)
                                .And(Infer.Field<GameSearchDocument>(doc => doc.Description)))
                            .Fuzziness(new Fuzziness("AUTO"))
                            .PrefixLength(1)
                            .Operator(Operator.Or)))
                    .Filter(f => f
                        .Term(t => t
                            .Field(doc => doc.IsActive)
                            .Value(true)))))
            .Sort(sort => sort
                .Score(new ScoreSort { Order = SortOrder.Desc })),
            cancellationToken);

        if (!response.IsValidResponse)
        {
            if (response.ElasticsearchServerError?.Status == 404)
            {
                return [];
            }

            logger.LogWarning(
                "Consulta no Elasticsearch retornou erro: {DebugInformation}",
                response.DebugInformation);
            return [];
        }

        return response.Hits
            .Where(hit => hit.Source is not null)
            .Select(hit => new GameSearchResult(
                hit.Source!.Id,
                hit.Source.Title,
                hit.Source.Description,
                hit.Source.Price,
                hit.Score ?? 0d))
            .ToList();
    }
}

public sealed class ElasticsearchIndexInitializerHostedService(
    IServiceScopeFactory scopeFactory,
    ILogger<ElasticsearchIndexInitializerHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var catalogService = scope.ServiceProvider.GetRequiredService<CatalogService>();
            var result = await catalogService.ReindexGamesAsync(cancellationToken);
            logger.LogInformation(
                "Sincronizacao inicial do Elasticsearch concluida ({IndexedCount} jogos ativos indexados).",
                result.IndexedCount);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Falha nao-fatal durante a sincronizacao inicial do indice no Elasticsearch.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}


