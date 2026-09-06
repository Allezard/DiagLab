using System.Collections.Concurrent;

namespace LoadGen;

/// <summary>Счётчики одного среза: весь прогон, отдельный сценарий или один интервал живой статистики.</summary>
public sealed class Stats
{
    private long _sent;
    private long _ok;
    private long _failed;
    private long _skipped;
    private long _abandoned;
    private long _bytes;

    public Histogram Latency { get; } = new();
    public ConcurrentDictionary<string, long> Errors { get; } = new();

    public long Sent => Interlocked.Read(ref _sent);
    public long Ok => Interlocked.Read(ref _ok);
    public long Failed => Interlocked.Read(ref _failed);
    public long Skipped => Interlocked.Read(ref _skipped);
    public long Abandoned => Interlocked.Read(ref _abandoned);
    public long Bytes => Interlocked.Read(ref _bytes);
    public long Completed => Ok + Failed;

    public void OnSent() => Interlocked.Increment(ref _sent);
    public void OnSkipped() => Interlocked.Increment(ref _skipped);
    public void OnAbandoned() => Interlocked.Increment(ref _abandoned);

    public void OnOk(long micros, long bytes)
    {
        Interlocked.Increment(ref _ok);
        Interlocked.Add(ref _bytes, bytes);
        Latency.Record(micros);
    }

    /// <summary>
    /// Ошибки в гистограмму задержек не попадают: отказ в соединении за 0,1 мс
    /// и таймаут за 30 с одинаково исказили бы перцентили успешных ответов.
    /// </summary>
    public void OnFailed(long micros, string kind)
    {
        Interlocked.Increment(ref _failed);
        Errors.AddOrUpdate(kind, 1, static (_, n) => n + 1);
    }
}
