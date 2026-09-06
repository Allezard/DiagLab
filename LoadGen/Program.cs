using System.Globalization;
using System.Text;
using LoadGen;

if (OperatingSystem.IsWindows())
    Console.OutputEncoding = Encoding.UTF8;

CliOptions cli;
try
{
    cli = CliOptions.Parse(args);
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine($"Ошибка в параметрах: {ex.Message}");
    Console.Error.WriteLine("Справка: loadgen --help");
    return 2;
}

if (cli.Help)
{
    CliOptions.PrintHelp();
    return 0;
}

LoadGenConfig config;
try
{
    config = LoadGenConfig.Load(cli.ConfigPath);
}
catch (ConfigException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

if (cli.List)
{
    PrintScenarios(config);
    return 0;
}

// ---------- сценарий ----------

Scenario scenario;
try
{
    scenario = ResolveScenario(cli, config);
}
catch (Exception ex) when (ex is ConfigException or FormatException)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

var baseUrl = cli.BaseUrl ?? config.BaseUrl;
if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri))
{
    Console.Error.WriteLine($"Некорректный адрес сервиса: {baseUrl}");
    return 2;
}

// Модель нагрузки: явный ключ важнее значения по умолчанию из сценария.
var rps = cli.Rps > 0 ? cli.Rps
    : !cli.ConcurrencySet && scenario.DefaultRps is { } scenarioRps ? scenarioRps
    : 0;
var concurrency = cli.ConcurrencySet ? cli.Concurrency : scenario.DefaultConcurrency ?? cli.Concurrency;

var options = new RunOptions
{
    Scenario = scenario,
    BaseUri = baseUri,
    Model = rps > 0 ? LoadModel.Open : LoadModel.Closed,
    Concurrency = concurrency,
    Rps = rps,
    MaxInFlight = cli.MaxInFlight,
    Requests = cli.Requests,
    // -n без -d: до выполнения всех запросов. Иначе длительность по умолчанию — 60 секунд.
    Duration = cli.Duration ?? (cli.Requests is null ? TimeSpan.FromSeconds(60) : null),
    Warmup = cli.Warmup,
    Delay = cli.Delay,
    RequestTimeout = cli.Timeout,
    ReportInterval = cli.Interval,
};

// ---------- проверка доступности ----------

if (!cli.NoHealthCheck && !string.IsNullOrEmpty(config.HealthCheck))
{
    using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
    try
    {
        using var response = await probe.GetAsync(new Uri(baseUri, config.HealthCheck));
        response.EnsureSuccessStatusCode();
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Сервис недоступен: {new Uri(baseUri, config.HealthCheck)} ({ex.GetBaseException().Message})");
        if (!string.IsNullOrEmpty(config.StartHint))
            Console.Error.WriteLine(config.StartHint);
        Console.Error.WriteLine("Проверку можно отключить ключом --no-health.");
        return 1;
    }
}

// ---------- запуск ----------

using var runner = new Runner(options);
using var stop = new CancellationTokenSource();
var interrupts = 0;

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    if (Interlocked.Increment(ref interrupts) == 1)
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine("Останавливаюсь: новые запросы не отправляются, жду ответов в полёте. Ещё раз Ctrl+C — прервать сразу.");
        stop.Cancel();
    }
    else
    {
        runner.Abort();
    }
};

PrintBanner(options, runner.RunId, config);

var result = await runner.RunAsync(stop.Token);

Report.PrintSummary(result);

if (cli.JsonPath is not null)
{
    Report.WriteJson(result, cli.JsonPath);
    Console.WriteLine($"Итог сохранён: {Path.GetFullPath(cli.JsonPath)}");
}

return result.ServiceDown ? 3 : 0;

// ---------- вспомогательное ----------

static Scenario ResolveScenario(CliOptions cli, LoadGenConfig config)
{
    if (cli.Url is not null)
    {
        return new Scenario
        {
            Name = "url",
            Description = cli.Url,
            Request = new RequestSpec
            {
                Method = cli.Method.ToUpperInvariant(),
                Target = Template.Parse(cli.Url),
                Body = cli.Body is null ? null : Template.Parse(cli.Body),
                ContentType = cli.ContentType,
            },
        };
    }

    var name = cli.Scenario ?? config.DefaultScenario;
    if (name is null)
    {
        throw new ConfigException(config.Scenarios.Count == 0
            ? $"Нет файла сценариев ({LoadGenConfig.DefaultFileName}). Задайте запрос ключом --url или укажите файл: --config <путь>."
            : "Не указан сценарий. Список: loadgen --list");
    }

    if (!config.Scenarios.TryGetValue(name, out var scenario))
    {
        var known = string.Join(", ", config.Scenarios.Keys.Order(StringComparer.Ordinal));
        throw new ConfigException($"Сценарий «{name}» не найден. Доступны: {known}");
    }

    return scenario;
}

static void PrintScenarios(LoadGenConfig config)
{
    if (config.Scenarios.Count == 0)
    {
        Console.WriteLine($"Файл сценариев не найден ({LoadGenConfig.DefaultFileName}). Используйте --url или --config.");
        return;
    }

    Console.WriteLine($"Сценарии из {config.SourcePath}");
    Console.WriteLine($"Сервис по умолчанию: {config.BaseUrl}");
    Console.WriteLine();

    foreach (var s in config.Scenarios.Values.OrderBy(s => s.IsMix).ThenBy(s => s.Name, StringComparer.Ordinal))
    {
        var marker = s.Name.Equals(config.DefaultScenario, StringComparison.OrdinalIgnoreCase) ? " (по умолчанию)" : "";
        var what = s.IsMix
            ? "смесь: " + string.Join(", ", s.Mix.Select(m => m.Weight == 1 ? m.Scenario.Name : $"{m.Scenario.Name}×{m.Weight}"))
            : $"{s.Request!.Method} {s.Request.Target}";

        Console.WriteLine($"  {s.Name + marker,-24} {s.Description}");
        Console.WriteLine($"  {"",-24} {what}");
    }
}

static void PrintBanner(RunOptions o, string runId, LoadGenConfig config)
{
    var limits = new List<string>();
    if (o.Duration is { } d) limits.Add($"{d.TotalSeconds.ToString("N0", CultureInfo.CurrentCulture)} с");
    if (o.Requests is { } n) limits.Add($"{n.ToString("N0", CultureInfo.CurrentCulture)} запросов");
    if (o.Warmup > TimeSpan.Zero) limits.Add($"прогрев {o.Warmup.TotalSeconds:N0} с");

    Console.WriteLine($"LoadGen → {o.BaseUri}");
    Console.WriteLine($"Сценарий: {o.Scenario.Name} | модель: {Report.DescribeModel(o)} | {string.Join(", ", limits)} | запуск {runId}");
    Console.WriteLine("Остановка: Ctrl+C");
}