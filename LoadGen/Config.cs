using System.Text.Json;

namespace LoadGen;

/// <summary>Один запрос сценария: метод, путь или адрес, тело и заголовки — всё шаблоны.</summary>
public sealed class RequestSpec
{
    public required string Method { get; init; }
    public required Template Target { get; init; }
    public Template? Body { get; init; }
    public string ContentType { get; init; } = "application/json";
    public IReadOnlyDictionary<string, Template> Headers { get; init; } = new Dictionary<string, Template>();
}

/// <summary>
/// Сценарий — либо один запрос, либо смесь других сценариев с весами.
/// </summary>
public sealed class Scenario
{
    public required string Name { get; init; }
    public string Description { get; init; } = string.Empty;
    public RequestSpec? Request { get; init; }
    public IReadOnlyList<(Scenario Scenario, int Weight)> Mix { get; init; } = [];

    /// <summary>Частота по умолчанию (открытая модель), если в командной строке не заданы ни -c, ни --rps.</summary>
    public double? DefaultRps { get; init; }

    /// <summary>Число клиентов по умолчанию (закрытая модель), если в командной строке не заданы ни -c, ни --rps.</summary>
    public int? DefaultConcurrency { get; init; }

    public bool IsMix => Mix.Count > 0;

    private int _totalWeight;

    /// <summary>Выбрать конкретный сценарий с запросом: для смеси — случайно по весам.</summary>
    public Scenario Pick()
    {
        if (!IsMix)
            return this;

        if (_totalWeight == 0)
            _totalWeight = Mix.Sum(m => m.Weight);

        var roll = Random.Shared.Next(_totalWeight);
        foreach (var (scenario, weight) in Mix)
        {
            if (roll < weight)
                return scenario.Pick();
            roll -= weight;
        }

        return Mix[^1].Scenario.Pick();
    }

    /// <summary>Все листовые сценарии, в которые может развернуться этот (для статистики по частям).</summary>
    public IEnumerable<Scenario> Leaves() =>
        IsMix ? Mix.SelectMany(m => m.Scenario.Leaves()).Distinct() : [this];
}

public sealed class LoadGenConfig
{
    public string BaseUrl { get; init; } = "http://localhost:5000";
    public string? HealthCheck { get; init; }
    public string? StartHint { get; init; }
    public string? DefaultScenario { get; init; }
    public IReadOnlyDictionary<string, Scenario> Scenarios { get; init; } = new Dictionary<string, Scenario>();
    public string? SourcePath { get; init; }

    public const string DefaultFileName = "loadgen.json";

    /// <summary>
    /// Ищет файл сценариев: явно указанный, затем в текущем каталоге, затем рядом с исполняемым файлом.
    /// </summary>
    public static LoadGenConfig Load(string? explicitPath)
    {
        string? path = explicitPath;

        if (path is null)
        {
            var candidates = new[]
            {
                Path.Combine(Environment.CurrentDirectory, DefaultFileName),
                Path.Combine(AppContext.BaseDirectory, DefaultFileName),
            };
            path = candidates.FirstOrDefault(File.Exists);
        }

        if (path is null)
            return new LoadGenConfig();

        if (!File.Exists(path))
            throw new ConfigException($"Файл сценариев не найден: {path}");

        try
        {
            using var stream = File.OpenRead(path);
            using var doc = JsonDocument.Parse(stream, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });

            return Parse(doc.RootElement, Path.GetFullPath(path));
        }
        catch (JsonException ex)
        {
            throw new ConfigException($"Ошибка в {path}: {ex.Message}");
        }
        catch (FormatException ex)
        {
            throw new ConfigException($"Ошибка в {path}: {ex.Message}");
        }
    }

    private static LoadGenConfig Parse(JsonElement root, string path)
    {
        var raw = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("scenarios", out var scenariosElement))
        {
            foreach (var property in scenariosElement.EnumerateObject())
                raw[property.Name] = property.Value;
        }

        var built = new Dictionary<string, Scenario>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in raw.Keys)
            BuildScenario(name, raw, built, []);

        return new LoadGenConfig
        {
            BaseUrl = GetString(root, "baseUrl") ?? "http://localhost:5000",
            HealthCheck = GetString(root, "healthCheck"),
            StartHint = GetString(root, "startHint"),
            DefaultScenario = GetString(root, "defaultScenario"),
            Scenarios = built,
            SourcePath = path,
        };
    }

    private static Scenario BuildScenario(
        string name,
        Dictionary<string, JsonElement> raw,
        Dictionary<string, Scenario> built,
        HashSet<string> visiting)
    {
        if (built.TryGetValue(name, out var existing))
            return existing;

        if (!raw.TryGetValue(name, out var element))
            throw new ConfigException($"Смесь ссылается на несуществующий сценарий «{name}».");

        if (!visiting.Add(name))
            throw new ConfigException($"Циклическая ссылка в смеси сценариев: {string.Join(" → ", visiting)} → {name}.");

        Scenario scenario;
        var description = GetString(element, "description") ?? string.Empty;
        double? defaultRps = element.TryGetProperty("rps", out var rpsElement) ? rpsElement.GetDouble() : null;
        int? defaultConcurrency = element.TryGetProperty("concurrency", out var cElement) ? cElement.GetInt32() : null;

        if (defaultRps is <= 0 || defaultConcurrency is <= 0)
            throw new ConfigException($"Сценарий «{name}»: rps и concurrency должны быть положительными.");
        if (defaultRps is not null && defaultConcurrency is not null)
            throw new ConfigException($"Сценарий «{name}»: укажите либо rps (открытая модель), либо concurrency (закрытая), но не оба.");

        if (element.TryGetProperty("mix", out var mixElement))
        {
            var mix = new List<(Scenario, int)>();

            if (mixElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in mixElement.EnumerateArray())
                    mix.Add((BuildScenario(item.GetString()!, raw, built, visiting), 1));
            }
            else
            {
                foreach (var item in mixElement.EnumerateObject())
                {
                    var weight = item.Value.GetInt32();
                    if (weight <= 0)
                        throw new ConfigException($"Сценарий «{name}»: вес «{item.Name}» должен быть положительным.");
                    mix.Add((BuildScenario(item.Name, raw, built, visiting), weight));
                }
            }

            if (mix.Count == 0)
                throw new ConfigException($"Сценарий «{name}»: пустая смесь.");

            scenario = new Scenario
            {
                Name = name, Description = description, Mix = mix,
                DefaultRps = defaultRps, DefaultConcurrency = defaultConcurrency,
            };
        }
        else
        {
            scenario = new Scenario
            {
                Name = name,
                Description = description,
                Request = ParseRequest(name, element),
                DefaultRps = defaultRps,
                DefaultConcurrency = defaultConcurrency,
            };
        }

        visiting.Remove(name);
        built[name] = scenario;
        return scenario;
    }

    private static RequestSpec ParseRequest(string name, JsonElement element)
    {
        var target = GetString(element, "path") ?? GetString(element, "url")
            ?? throw new ConfigException($"Сценарий «{name}»: нужен «path» (или «url») либо «mix».");

        var headers = new Dictionary<string, Template>(StringComparer.OrdinalIgnoreCase);
        if (element.TryGetProperty("headers", out var headersElement))
        {
            foreach (var header in headersElement.EnumerateObject())
                headers[header.Name] = Template.Parse(header.Value.GetString() ?? string.Empty);
        }

        var body = element.TryGetProperty("body", out var bodyElement)
            ? bodyElement.ValueKind == JsonValueKind.String ? bodyElement.GetString() : bodyElement.GetRawText()
            : null;

        return new RequestSpec
        {
            Method = (GetString(element, "method") ?? "GET").ToUpperInvariant(),
            Target = Template.Parse(target),
            Body = body is null ? null : Template.Parse(body),
            ContentType = GetString(element, "contentType") ?? "application/json",
            Headers = headers,
        };
    }

    private static string? GetString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

public sealed class ConfigException(string message) : Exception(message);
