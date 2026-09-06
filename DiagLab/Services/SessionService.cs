using System.Collections.Concurrent;

namespace DiagLab.Services;

public sealed class UserSession
{
    public required string SessionId { get; init; }
    public required string UserId { get; init; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime LastSeen { get; set; } = DateTime.UtcNow;

    // Корзина и просмотренные товары
    public List<Product> Basket { get; } = new();
    public List<int> ViewedIds { get; } = new();
}

/// <summary>
/// Хранилище пользовательских сессий.
/// </summary>
public sealed class SessionService
{
    private static readonly ConcurrentDictionary<string, UserSession> Sessions = new();
    private static readonly ConcurrentBag<Timer> Keepalives = new();

    public UserSession Open(string userId)
    {
        var session = new UserSession
        {
            SessionId = Guid.NewGuid().ToString("N"),
            UserId = userId
        };

        Sessions[session.SessionId] = session;

        // Периодически продлеваем сессию, пока пользователь активен
        var timer = new Timer(_ => session.LastSeen = DateTime.UtcNow,
                              null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        Keepalives.Add(timer);

        return session;
    }

    public bool Close(string sessionId) => Sessions.TryRemove(sessionId, out _);

    public UserSession? Get(string sessionId) => Sessions.GetValueOrDefault(sessionId);

    public int ActiveCount => Sessions.Count;
}