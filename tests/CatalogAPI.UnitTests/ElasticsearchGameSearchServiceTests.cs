using System.Text;
using CatalogAPI;
using Elastic.Clients.Elasticsearch;
using Elastic.Transport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CatalogAPI.UnitTests;

public sealed class ElasticsearchGameSearchServiceTests
{
    [Fact]
    public async Task IndexSearchAndRemove_SerializesRequestsAndParsesHitsOrderedByScore()
    {
        var gameId1 = Guid.NewGuid();
        var gameId2 = Guid.NewGuid();
        var searchResponseJson = $$"""
        {
          "took": 2,
          "timed_out": false,
          "_shards": { "total": 1, "successful": 1, "skipped": 0, "failed": 0 },
          "hits": {
            "total": { "value": 2, "relation": "eq" },
            "max_score": 4.25,
            "hits": [
              {
                "_index": "fcg-games",
                "_id": "{{gameId1}}",
                "_score": 4.25,
                "_source": {
                  "id": "{{gameId1}}",
                  "title": "Cyber FIAP",
                  "description": "Jogo futurista de acao",
                  "price": 99.90,
                  "isActive": true
                }
              },
              {
                "_index": "fcg-games",
                "_id": "{{gameId2}}",
                "_score": 1.75,
                "_source": {
                  "id": "{{gameId2}}",
                  "title": "Adventure FIAP",
                  "description": "Inspirado no universo Cyber",
                  "price": 49.90,
                  "isActive": true
                }
              }
            ]
          }
        }
        """;

        var invoker = new InMemoryRequestInvoker(
            Encoding.UTF8.GetBytes(searchResponseJson),
            headers: new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["x-elastic-product"] = ["Elasticsearch"],
                ["X-Elastic-Product"] = ["Elasticsearch"]
            });
        var settings = new ElasticsearchClientSettings(invoker);
        var client = new ElasticsearchClient(settings);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Elasticsearch:Index"] = "fcg-games"
            })
            .Build();

        var sut = new ElasticsearchGameSearchService(
            client,
            config,
            NullLogger<ElasticsearchGameSearchService>.Instance);

        var game = new Game
        {
            Id = gameId1,
            Title = "Cyber FIAP",
            Description = "Jogo futurista de acao",
            Price = 99.90m,
            IsActive = true
        };

        await sut.EnsureIndexAsync(CancellationToken.None);
        await sut.IndexGameAsync(game, CancellationToken.None);
        var results = await sut.SearchAsync("Cybr", 10, CancellationToken.None);
        var emptyResults = await sut.SearchAsync("   ", 10, CancellationToken.None);
        await sut.RemoveGameAsync(gameId1, CancellationToken.None);

        Assert.Empty(emptyResults);
        Assert.Equal(2, results.Count);
        Assert.Equal(gameId1, results[0].Id);
        Assert.Equal(4.25, results[0].Score);
        Assert.Equal(gameId2, results[1].Id);
        Assert.Equal(1.75, results[1].Score);
    }

    [SkippableFact]
    public async Task LiveElasticsearch_SupportsFuzzySearchAndRelevanceOrdering()
    {
        var elasticUrl = Environment.GetEnvironmentVariable("FCG_TEST_ELASTICSEARCH_URL");
        Skip.If(string.IsNullOrWhiteSpace(elasticUrl), "FCG_TEST_ELASTICSEARCH_URL is not configured.");

        var indexName = $"fcg-games-tests-{Guid.NewGuid():N}";
        var client = new ElasticsearchClient(new ElasticsearchClientSettings(new Uri(elasticUrl)));
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Elasticsearch:Index"] = indexName
            })
            .Build();
        var sut = new ElasticsearchGameSearchService(
            client,
            config,
            NullLogger<ElasticsearchGameSearchService>.Instance);

        var game1 = new Game
        {
            Id = Guid.NewGuid(),
            Title = "Cyber FIAP Remasterizado",
            Description = "RPG Cyber FIAP com graficos de nova geracao",
            Price = 129.90m,
            IsActive = true
        };
        var game2 = new Game
        {
            Id = Guid.NewGuid(),
            Title = "Corrida Espacial",
            Description = "Inclui pista tematica Cyber",
            Price = 59.90m,
            IsActive = true
        };

        try
        {
            await sut.IndexGameAsync(game1, CancellationToken.None);
            await sut.IndexGameAsync(game2, CancellationToken.None);

            var fuzzyResults = await sut.SearchAsync("Cybr", 10, CancellationToken.None);
            Assert.NotEmpty(fuzzyResults);
            Assert.Equal(game1.Id, fuzzyResults[0].Id);

            await sut.RemoveGameAsync(game1.Id, CancellationToken.None);
            var afterRemove = await sut.SearchAsync("Remasterizado", 10, CancellationToken.None);
            Assert.DoesNotContain(afterRemove, r => r.Id == game1.Id);
        }
        finally
        {
            await client.Indices.DeleteAsync(indexName);
        }
    }
}
