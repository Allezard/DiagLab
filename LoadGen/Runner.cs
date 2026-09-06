using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace LoadGen;

public enum LoadModel
{
    /// <summary>N клиентов, каждый шлёт следующий запрос только после ответа на предыдущий.</summary>
    Closed,

    /// <summary>Запросы идут с заданной частотой независимо от того, успевает ли сервис отвечать.</summary>
    Open,
}

public sealed class RunOptions
{
    public required Scenario Scenario { get; init; }
    public required Uri BaseUri { get; init; }
    public LoadModel Model { get; init; } = LoadModel.Closed;
    public int Concurrency { get; init; } = 8;
    public double Rps { get; init; }
    public int MaxInFlight { get; init; } = 1000;
    public TimeSpan? Duration { get; init; }
    public long? Requests { get; init; }
    public TimeSpan Warmup { get; init; }
    public TimeSpan Delay { get; init; }
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan ReportInterval { get; init; } = TimeSpan.FromSeconds(1);
}

public sealed class RunResult
{
    public required RunOptions Options { get; init; }
    public required Stats Total { get; init; }
    public required IReadOnlyDictionary<string, Stats> ByScenario { get; init; }
    public required TimeSpan Measured { get; init; }
    public required string RunId { get; init; }
    public bool Interrupted { get; init; }

    /// <summary>Сервис перестал принимать соединения, и генерация остановлена досрочно.</summary>
    public bool ServiceDown { get; init; }
}

public sealed class Runner : IDisposable
{
    private readonly RunOptions _options;
    private readonly HttpClient _client;
    private readonly string _runId = Convert.ToHexString(BitConverter.GetBytes(Random.Shared.Next()))[..4].ToLowerInvariant();

    private readonly Stats _total = new();
    private readonly Dictionary<string, Stats> _byScenario;
    private Stats _interval = new();

    private readonly CancellationTokenSource _abort = new();
    private long _sequence;
    private long _budgetTaken;
    private long _inFlight;
    private long _measureStart;

    // Наблюдение за доступностью: время последнего полученного ответа (любого, в том числе с ошибкой HTTP)
    // и время первой ошибки соединения после него. Если соединения отклоняются несколько секунд подряд
    // и ни одного ответа нет, сервис, скорее всего, упал — лить нагрузку дальше бессмысленно.
    private long _lastResponse;
    private long _firstRefusal;
    private volatile bool _serviceDown;
    private static readonly TimeSpan DownAfter = TimeSpan.FromSeconds(3);

    public Runner(RunOptions options)
    {
        _options = options;
        _byScenario = options.Scenario.Leaves().ToDictionary(s => s.Name, _ => new Stats(), StringComparer.OrdinalIgnoreCase);

        // Один клиент на весь прогон: соединения переиспользуются (keep-alive).
        // Генератор не должен сам открывать соединение на каждый запрос — иначе сокеты
        // в TIME_WAIT от генератора смешаются с сокетами исследуемого сервиса.
        var handler = new SocketsHttpHandler
        {
            MaxConnectionsPerServer = int.MaxValue,
            UseCookies = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(10),
        };

        _client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("DiagLab-LoadGen/1.0");
    }

    public string RunId => _runId;
    public long InFlight => Interlocked.Read(ref _inFlight);

    public async Task<RunResult> RunAsync(CancellationToken stop)
    {
        var started = Stopwatch.GetTimestamp();
        _measureStart = started + ToTicks(_options.Warmup);
        Interlocked.Exchange(ref _lastResponse, started);

        using var generation = CancellationTokenSource.CreateLinkedTokenSource(stop);
        if (_options.Duration is { } duration)
            generation.CancelAfter(_options.Warmup + duration);

        using var watchdogStop = new CancellationTokenSource();
        var watchdog = WatchServiceAsync(generation, watchdogStop.Token);

        using var reporterStop = new CancellationTokenSource();
        var reporter = _options.ReportInterval > TimeSpan.Zero
            ? Task.Run(() => ReportLoopAsync(started, reporterStop.Token))
            : Task.CompletedTask;

        var pending = _options.Model == LoadModel.Closed
            ? RunClosed(generation.Token)
            : await RunOpenAsync(generation.Token);

        // Сначала ждём конца генерации (время вышло, запросы кончились или Ctrl+C),
        // и только потом даём ограниченное время на ответы, оставшиеся в полёте.
        await WaitForGenerationEndAsync(pending, generation.Token);
        var generationEnded = Stopwatch.GetTimestamp();
        await DrainAsync(pending);

        // Длительность — время генерации без прогрева и без ожидания ответов в полёте:
        // иначе частота запросов в итоге занижалась бы на время ожидания.
        var measured = Stopwatch.GetElapsedTime(Math.Min(_measureStart, generationEnded), generationEnded);

        await reporterStop.CancelAsync();
        await reporter;
        await watchdogStop.CancelAsync();
        await watchdog;

        return new RunResult
        {
            Options = _options,
            Total = _total,
            ByScenario = _byScenario,
            Measured = measured,
            RunId = _runId,
            Interrupted = stop.IsCancellationRequested,
            ServiceDown = _serviceDown,
        };
    }

    public void Abort() => _abort.Cancel();

    private async Task WatchServiceAsync(CancellationTokenSource generation, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && !generation.IsCancellationRequested)
            {
                await Task.Delay(250, ct);

                var refusal = Interlocked.Read(ref _firstRefusal);
                if (refusal == 0)
                    continue;

                var sinceResponse = Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastResponse));
                if (Stopwatch.GetElapsedTime(refusal) >= DownAfter && sinceResponse >= DownAfter)
                {
                    _serviceDown = true;
                    Console.Error.WriteLine();
                    Console.Error.WriteLine($"Сервис не принимает соединения уже {sinceResponse.TotalSeconds:N0} с — останавливаю нагрузку.");
                    Console.Error.WriteLine("Проверьте, жив ли процесс сервиса, и посмотрите, что он написал в консоль перед остановкой.");
                    await generation.CancelAsync();
                    await _abort.CancelAsync();
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    // ---------- модели нагрузки ----------

    private Task RunClosed(CancellationToken generation)
    {
        var workers = new Task[_options.Concurrency];
        for (var i = 0; i < workers.Length; i++)
        {
            workers[i] = Task.Run(async () =>
            {
                while (!generation.IsCancellationRequested && !_abort.IsCancellationRequested && TryTakeBudget())
                {
                    Interlocked.Increment(ref _inFlight);
                    try
                    {
                        await ExecuteAsync(_options.Scenario.Pick(), Stopwatch.GetTimestamp());
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _inFlight);
                    }

                    if (_options.Delay > TimeSpan.Zero)
                    {
                        try { await Task.Delay(_options.Delay, generation); }
                        catch (OperationCanceledException) { break; }
                    }
                }
            });
        }

        return Task.WhenAll(workers);
    }

    private async Task<Task> RunOpenAsync(CancellationToken generation)
    {
        var started = Stopwatch.GetTimestamp();
        var ticksPerRequest = Stopwatch.Frequency / _options.Rps;
        long issued = 0;
        var running = new List<Task>();

        while (!generation.IsCancellationRequested)
        {
            // Сколько запросов должно было уйти к этому моменту. Таймер ОС неточен
            // (на Windows ~15 мс), поэтому отставание догоняется пачкой, а время
            // «когда запрос должен был уйти» у каждого своё — от него и считается задержка.
            var due = (long)((Stopwatch.GetTimestamp() - started) / ticksPerRequest) + 1;
            var budgetEnded = false;

            while (issued < due)
            {
                if (!TryTakeBudget())
                {
                    budgetEnded = true;
                    break;
                }

                var intended = started + (long)(issued * ticksPerRequest);
                issued++;

                if (Interlocked.Read(ref _inFlight) >= _options.MaxInFlight)
                {
                    var target = IsMeasured(intended) ? _total : null;
                    target?.OnSkipped();
                    Volatile.Read(ref _interval).OnSkipped();
                    continue;
                }

                Interlocked.Increment(ref _inFlight);
                running.Add(RunTrackedAsync(_options.Scenario.Pick(), intended));
            }

            if (budgetEnded)
                break;

            if (running.Count > 4096)
                running.RemoveAll(t => t.IsCompleted);

            try { await Task.Delay(1, generation); }
            catch (OperationCanceledException) { break; }
        }

        return Task.WhenAll(running);
    }

    private async Task RunTrackedAsync(Scenario scenario, long intended)
    {
        try
        {
            await ExecuteAsync(scenario, intended);
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    private bool TryTakeBudget()
    {
        if (_options.Requests is not { } limit)
            return true;

        return Interlocked.Increment(ref _budgetTaken) <= limit;
    }

    /// <summary>Дать завершиться запросам в полёте, но не дольше таймаута запроса и не дольше 10 секунд.</summary>
    private async Task DrainAsync(Task pending)
    {
        var limit = TimeSpan.FromSeconds(Math.Min(10, _options.RequestTimeout.TotalSeconds));
        var finished = await Task.WhenAny(pending, Task.Delay(limit));
        if (finished != pending)
        {
            await _abort.CancelAsync();
        }

        try { await pending; }
        catch (OperationCanceledException) { }
    }

    private static async Task WaitForGenerationEndAsync(Task pending, CancellationToken generation)
    {
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var registration = generation.Register(() => stopped.TrySetResult());
        await Task.WhenAny(pending, stopped.Task);
    }

    // ---------- один запрос ----------

    private async Task ExecuteAsync(Scenario scenario, long intended)
    {
        var spec = scenario.Request!;
        var context = new RenderContext(Interlocked.Increment(ref _sequence), _runId);

        var measured = IsMeasured(intended);
        var scenarioStats = measured ? _byScenario[scenario.Name] : null;
        var totalStats = measured ? _total : null;
        var interval = Volatile.Read(ref _interval);

        totalStats?.OnSent();
        scenarioStats?.OnSent();
        interval.OnSent();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_abort.Token);
        timeout.CancelAfter(_options.RequestTimeout);

        long bytes = 0;
        string? error = null;

        try
        {
            using var request = BuildRequest(spec, context);
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);

            // Ответ читается целиком: клиент ждёт последний байт, а не только заголовки.
            bytes = await DrainBodyAsync(response.Content, timeout.Token);

            Interlocked.Exchange(ref _lastResponse, Stopwatch.GetTimestamp());
            Interlocked.Exchange(ref _firstRefusal, 0);

            if (!response.IsSuccessStatusCode)
                error = $"HTTP {(int)response.StatusCode}";
        }
        catch (OperationCanceledException) when (_abort.IsCancellationRequested)
        {
            interval.OnAbandoned();
            totalStats?.OnAbandoned();
            scenarioStats?.OnAbandoned();
            return;
        }
        catch (OperationCanceledException)
        {
            error = "таймаут";
        }
        catch (HttpRequestException ex)
        {
            error = DescribeConnectionError(ex);
            if (ex.HttpRequestError == HttpRequestError.ConnectionError)
                Interlocked.CompareExchange(ref _firstRefusal, Stopwatch.GetTimestamp(), 0);
        }
        catch (Exception ex)
        {
            error = ex.GetType().Name;
        }

        var micros = (long)Stopwatch.GetElapsedTime(intended).TotalMicroseconds;

        if (error is null)
        {
            interval.OnOk(micros, bytes);
            totalStats?.OnOk(micros, bytes);
            scenarioStats?.OnOk(micros, bytes);
        }
        else
        {
            interval.OnFailed(micros, error);
            totalStats?.OnFailed(micros, error);
            scenarioStats?.OnFailed(micros, error);
        }
    }

    private HttpRequestMessage BuildRequest(RequestSpec spec, RenderContext context)
    {
        var target = spec.Target.Render(context);

        // "/api/x" на Unix — это абсолютный путь к файлу, поэтому проверяем схему явно.
        var uri = Uri.TryCreate(target, UriKind.Absolute, out var absolute)
                  && (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps)
            ? absolute
            : new Uri(_options.BaseUri, target);

        var request = new HttpRequestMessage(new HttpMethod(spec.Method), uri);

        if (spec.Body is not null)
            request.Content = new StringContent(spec.Body.Render(context), Encoding.UTF8, spec.ContentType);

        foreach (var (name, value) in spec.Headers)
        {
            var rendered = value.Render(context);
            if (!request.Headers.TryAddWithoutValidation(name, rendered))
                request.Content?.Headers.TryAddWithoutValidation(name, rendered);
        }

        return request;
    }

    private static async Task<long> DrainBodyAsync(HttpContent content, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct);
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            long total = 0;
            int read;
            while ((read = await stream.ReadAsync(buffer, ct)) > 0)
                total += read;
            return total;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static string DescribeConnectionError(HttpRequestException ex)
    {
        if (ex.InnerException is SocketException socket)
            return $"сокет: {socket.SocketErrorCode}";

        return ex.HttpRequestError switch
        {
            HttpRequestError.ConnectionError => "ошибка соединения",
            HttpRequestError.ResponseEnded => "ответ оборван",
            HttpRequestError.NameResolutionError => "DNS",
            _ => "ошибка HTTP",
        };
    }

    // ---------- живая статистика ----------

    private async Task ReportLoopAsync(long started, CancellationToken ct)
    {
        Report.PrintIntervalHeader();

        using var timer = new PeriodicTimer(_options.ReportInterval);
        var last = Stopwatch.GetTimestamp();

        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                var now = Stopwatch.GetTimestamp();
                var slice = Interlocked.Exchange(ref _interval, new Stats());
                var seconds = Stopwatch.GetElapsedTime(last, now).TotalSeconds;
                last = now;

                Report.PrintInterval(
                    elapsed: Stopwatch.GetElapsedTime(started, now),
                    warmup: now < _measureStart,
                    slice,
                    seconds,
                    InFlight);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private bool IsMeasured(long timestamp) => timestamp >= _measureStart;

    private static long ToTicks(TimeSpan span) => (long)(span.TotalSeconds * Stopwatch.Frequency);

    public void Dispose()
    {
        _client.Dispose();
        _abort.Dispose();
    }
}
