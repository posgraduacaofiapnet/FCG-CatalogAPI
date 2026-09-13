using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;

namespace CatalogAPI;

public interface IGameCatalogCache
{
    Task<PagedResult<GameResponse>?> TryGetListAsync(PaginationParameters pagination, CancellationToken cancellationToken);
    Task SetListAsync(PaginationParameters pagination, PagedResult<GameResponse> value, CancellationToken cancellationToken);
    Task InvalidateListAsync(CancellationToken cancellationToken);
}

public sealed class DistributedGameCatalogCache(
    IDistributedCache cache,
    ILogger<DistributedGameCatalogCache> logger) : IGameCatalogCache
{
    public const string VersionKey = "catalog:games:ver";
    public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task<PagedResult<GameResponse>?> TryGetListAsync(
        PaginationParameters pagination,
        CancellationToken cancellationToken)
    {
        try
        {
            var version = await GetVersionAsync(cancellationToken);
            var json = await cache.GetStringAsync(ListKey(version, pagination), cancellationToken);
            if (string.IsNullOrEmpty(json))
            {
                logger.LogInformation("Cache miss da listagem de jogos. Page={Page} PageSize={PageSize}", pagination.Page, pagination.PageSize);
                return null;
            }

            var result = JsonSerializer.Deserialize<PagedResult<GameResponse>>(json, JsonOptions);
            logger.LogInformation("Cache hit da listagem de jogos. Page={Page} PageSize={PageSize}", pagination.Page, pagination.PageSize);
            return result;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Falha ao ler cache da listagem de jogos. Consultando o banco.");
            return null;
        }
    }

    public async Task SetListAsync(
        PaginationParameters pagination,
        PagedResult<GameResponse> value,
        CancellationToken cancellationToken)
    {
        try
        {
            var version = await GetVersionAsync(cancellationToken);
            var json = JsonSerializer.Serialize(value, JsonOptions);
            await cache.SetStringAsync(
                ListKey(version, pagination),
                json,
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = Ttl },
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Falha ao gravar cache da listagem de jogos.");
        }
    }

    public async Task InvalidateListAsync(CancellationToken cancellationToken)
    {
        try
        {
            var current = await GetVersionAsync(cancellationToken);
            var next = (current + 1).ToString();
            await cache.SetStringAsync(
                VersionKey,
                next,
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromDays(7) },
                cancellationToken);
            logger.LogInformation("Cache da listagem de jogos invalidado. Version={Version}", next);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Falha ao invalidar cache da listagem de jogos.");
        }
    }

    private async Task<int> GetVersionAsync(CancellationToken cancellationToken)
    {
        var raw = await cache.GetStringAsync(VersionKey, cancellationToken);
        return int.TryParse(raw, out var version) && version > 0 ? version : 1;
    }

    private static string ListKey(int version, PaginationParameters pagination) =>
        $"catalog:games:v{version}:p{pagination.Page}:s{pagination.PageSize}";
}
