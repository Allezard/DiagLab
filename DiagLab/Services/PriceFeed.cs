namespace DiagLab.Services;

public sealed class PriceChangedEventArgs : EventArgs
{
    public required string Sku { get; init; }
    public required decimal NewPrice { get; init; }
    public DateTime At { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Общая шина уведомлений об изменении цен.
/// </summary>
public static class PriceFeed
{
    public static event EventHandler<PriceChangedEventArgs>? PriceChanged;

    public static void Publish(string sku, decimal price)
        => PriceChanged?.Invoke(null, new PriceChangedEventArgs { Sku = sku, NewPrice = price });

    public static int SubscriberCount => PriceChanged?.GetInvocationList().Length ?? 0;
}

/// <summary>
/// Наблюдатель за ценами для конкретного пользователя.
/// Держит историю замеченных изменений, чтобы показать её в личном кабинете.
/// </summary>
public sealed class PriceWatcher
{
    private readonly List<PriceChangedEventArgs> _seen = new();
    private readonly byte[] _sessionState = new byte[32 * 1024];

    public string UserId { get; }
    public DateTime CreatedAt { get; } = DateTime.UtcNow;

    public PriceWatcher(string userId)
    {
        UserId = userId;
        Array.Fill(_sessionState, (byte)userId.Length);

        PriceFeed.PriceChanged += OnPriceChanged;
    }

    private void OnPriceChanged(object? sender, PriceChangedEventArgs e)
    {
        lock (_seen)
        {
            _seen.Add(e);
            if (_seen.Count > 500)
            {
                _seen.RemoveRange(0, 250);
            }
        }
    }

    public int SeenCount
    {
        get { lock (_seen) { return _seen.Count; } }
    }
}

/// <summary>
/// Фоновая публикация изменений цен.
/// </summary>
public sealed class PriceFeedWorker : BackgroundService
{
    private readonly int _intervalMs;

    public PriceFeedWorker(IConfiguration config)
        => _intervalMs = config.GetValue("DiagLab:PriceFeedIntervalMs", 1000);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var rnd = new Random(42);

        while (!stoppingToken.IsCancellationRequested)
        {
            PriceFeed.Publish($"SKU-{rnd.Next(1000):D6}", Math.Round((decimal)(rnd.NextDouble() * 1000), 2));
            await Task.Delay(_intervalMs, stoppingToken);
        }
    }
}