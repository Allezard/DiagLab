using System.Collections.Concurrent;

namespace DiagLab.Services;

public sealed record Product(int Id, string Sku, string Name, string Description, decimal Price);

public sealed record SearchResult(string Query, DateTime BuiltAt, IReadOnlyList<Product> Items);

/// <summary>
/// Поиск по каталогу. Результаты кэшируются, чтобы не пересобирать
/// одну и ту же выдачу при повторных запросах.
/// </summary>
public sealed class CatalogService
{
    // Кэш общий для всех запросов, поэтому статический.
    private static readonly ConcurrentDictionary<string, SearchResult> Cache = new();

    private static readonly string[] Categories =
        { "ноутбуки", "мониторы", "клавиатуры", "мыши", "докстанции", "кабели" };

    public SearchResult Search(string query)
    {
        var key = query.Trim().ToLowerInvariant();
        return Cache.GetOrAdd(key, BuildResult);
    }

    public int CacheSize => Cache.Count;

    private static SearchResult BuildResult(string query)
    {
        var rnd = new Random(query.GetHashCode());
        var items = new List<Product>(200);

        for (var i = 0; i < 200; i++)
        {
            var category = Categories[rnd.Next(Categories.Length)];

            items.Add(new Product(
                Id: rnd.Next(100_000),
                Sku: $"SKU-{rnd.Next(100_000):D6}",
                Name: $"{category} модель {rnd.Next(1000)}",
                Description: string.Create(512, rnd, static (span, r) =>
                {
                    for (var j = 0; j < span.Length; j++)
                        span[j] = (char)('а' + r.Next(32));
                }),
                Price: Math.Round((decimal)(rnd.NextDouble() * 5000), 2)));
        }

        return new SearchResult(query, DateTime.UtcNow, items);
    }
}