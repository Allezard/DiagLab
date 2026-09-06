using System.Globalization;
using System.Text.Json;

namespace LoadGen;

/// <summary>Вывод в консоль и в JSON.</summary>
public static class Report
{
    private static readonly CultureInfo Culture = CultureInfo.CurrentCulture;

    public static void PrintIntervalHeader()
    {
        Console.WriteLine();
        Console.WriteLine($"{"время",7} {"отпр/с",8} {"готово/с",9} {"в полёте",9} {"ошибки",7} {"p50",8} {"p95",8} {"p99",8} {"макс",8}");
    }

    public static void PrintInterval(TimeSpan elapsed, bool warmup, Stats slice, double seconds, long inFlight)
    {
        if (seconds <= 0) seconds = 1;

        var line =
            $"{FormatClock(elapsed),7} " +
            $"{Rate(slice.Sent + slice.Skipped, seconds),8} " +
            $"{Rate(slice.Completed, seconds),9} " +
            $"{inFlight.ToString("N0", Culture),9} " +
            $"{slice.Failed.ToString("N0", Culture),7} " +
            $"{Latency(slice.Latency.Percentile(50), slice.Ok),8} " +
            $"{Latency(slice.Latency.Percentile(95), slice.Ok),8} " +
            $"{Latency(slice.Latency.Percentile(99), slice.Ok),8} " +
            $"{Latency(slice.Latency.MaxMicros, slice.Ok),8}";

        if (warmup) line += "  прогрев";
        if (slice.Skipped > 0) line += $"  пропущено {slice.Skipped.ToString("N0", Culture)}";

        Console.WriteLine(line);
    }

    public static void PrintSummary(RunResult result)
    {
        var o = result.Options;
        var t = result.Total;
        var seconds = Math.Max(result.Measured.TotalSeconds, 0.001);

        Console.WriteLine();
        Console.WriteLine(result.ServiceDown ? "Итог (сервис перестал отвечать, нагрузка остановлена)"
            : result.Interrupted ? "Итог (остановлено вручную)" : "Итог");
        Console.WriteLine(new string('─', 60));
        Row("Сценарий", o.Scenario.Name);
        Row("Модель", DescribeModel(o));
        Row("Длительность", $"{seconds.ToString("N1", Culture)} с" +
                             (o.Warmup > TimeSpan.Zero ? $" (+ прогрев {o.Warmup.TotalSeconds.ToString("N0", Culture)} с)" : ""));
        Row("Отправлено", t.Sent.ToString("N0", Culture));
        Row("Успешных", t.Ok.ToString("N0", Culture));
        Row("Ошибок", t.Failed.ToString("N0", Culture) + DescribeErrors(t));

        if (t.Skipped > 0)
            Row("Пропущено", $"{t.Skipped.ToString("N0", Culture)}  ← в полёте было {o.MaxInFlight:N0}: сервис не успевал");

        if (t.Abandoned > 0)
            Row("Прервано", $"{t.Abandoned.ToString("N0", Culture)}  (не завершились к остановке)");

        Row("Запросов/с", (t.Completed / seconds).ToString("N1", Culture));
        Row("Получено", FormatBytes(t.Bytes));
        Row("Задержка", DescribeLatency(t) + (t.Ok > 0 ? "  (успешные ответы)" : ""));

        if (result.ByScenario.Count > 1)
        {
            Console.WriteLine();
            Console.WriteLine($"  {"сценарий",-12} {"успешных",9} {"ошибок",7} {"p50",8} {"p95",8} {"p99",8}");
            foreach (var (name, s) in result.ByScenario.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                if (s.Sent == 0) continue;
                Console.WriteLine(
                    $"  {name,-12} {s.Ok.ToString("N0", Culture),9} {s.Failed.ToString("N0", Culture),7} " +
                    $"{Latency(s.Latency.Percentile(50), s.Ok),8} " +
                    $"{Latency(s.Latency.Percentile(95), s.Ok),8} " +
                    $"{Latency(s.Latency.Percentile(99), s.Ok),8}");
            }
        }

        Console.WriteLine();
    }

    public static void WriteJson(RunResult result, string path)
    {
        using var stream = File.Create(path);
        using var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });

        var o = result.Options;
        var seconds = Math.Max(result.Measured.TotalSeconds, 0.001);

        w.WriteStartObject();
        w.WriteString("scenario", o.Scenario.Name);
        w.WriteString("runId", result.RunId);
        w.WriteString("model", o.Model == LoadModel.Closed ? "closed" : "open");
        w.WriteNumber("concurrency", o.Concurrency);
        w.WriteNumber("rps", o.Rps);
        w.WriteNumber("measuredSeconds", Math.Round(seconds, 3));
        w.WriteNumber("throughput", Math.Round(result.Total.Completed / seconds, 2));
        w.WriteBoolean("interrupted", result.Interrupted);
        w.WriteBoolean("serviceDown", result.ServiceDown);

        w.WritePropertyName("total");
        WriteStats(w, result.Total);

        w.WriteStartObject("scenarios");
        foreach (var (name, stats) in result.ByScenario)
        {
            w.WritePropertyName(name);
            WriteStats(w, stats);
        }
        w.WriteEndObject();

        w.WriteEndObject();
    }

    private static void WriteStats(Utf8JsonWriter w, Stats s)
    {
        w.WriteStartObject();
        w.WriteNumber("sent", s.Sent);
        w.WriteNumber("ok", s.Ok);
        w.WriteNumber("failed", s.Failed);
        w.WriteNumber("skipped", s.Skipped);
        w.WriteNumber("abandoned", s.Abandoned);
        w.WriteNumber("bytes", s.Bytes);

        w.WriteStartObject("latencyMs");
        foreach (var p in new[] { 50.0, 90, 95, 99, 99.9 })
            w.WriteNumber("p" + p.ToString(CultureInfo.InvariantCulture), Math.Round(s.Latency.Percentile(p) / 1000.0, 2));
        w.WriteNumber("max", Math.Round(s.Latency.MaxMicros / 1000.0, 2));
        w.WriteEndObject();

        w.WriteStartObject("errors");
        foreach (var (kind, count) in s.Errors)
            w.WriteNumber(kind, count);
        w.WriteEndObject();

        w.WriteEndObject();
    }

    public static string DescribeModel(RunOptions o) => o.Model == LoadModel.Closed
        ? $"закрытая, {o.Concurrency} клиент(ов)" + (o.Delay > TimeSpan.Zero ? $", пауза {o.Delay.TotalMilliseconds:N0} мс" : "")
        : $"открытая, {o.Rps.ToString("N1", Culture)} запросов/с, в полёте не больше {o.MaxInFlight:N0}";

    private static string DescribeErrors(Stats s)
    {
        if (s.Errors.IsEmpty) return string.Empty;
        var parts = s.Errors.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}: {kv.Value.ToString("N0", Culture)}");
        return "  (" + string.Join(", ", parts) + ")";
    }

    private static string DescribeLatency(Stats s)
    {
        if (s.Ok == 0) return "—";
        return $"p50 {Latency(s.Latency.Percentile(50), 1)} · p90 {Latency(s.Latency.Percentile(90), 1)} · " +
               $"p95 {Latency(s.Latency.Percentile(95), 1)} · p99 {Latency(s.Latency.Percentile(99), 1)} · " +
               $"макс {Latency(s.Latency.MaxMicros, 1)}";
    }

    private static void Row(string name, string value) => Console.WriteLine($"{name,-14}: {value}");

    private static string Rate(long count, double seconds) => (count / seconds).ToString("N1", Culture);

    public static string Latency(long micros, long samples)
    {
        if (samples == 0) return "—";
        var ms = micros / 1000.0;
        return ms switch
        {
            < 10 => ms.ToString("0.0", Culture) + " мс",
            < 1000 => ms.ToString("0", Culture) + " мс",
            _ => (ms / 1000).ToString("0.00", Culture) + " с",
        };
    }

    private static string FormatClock(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");

    public static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} Б",
        < 1024 * 1024 => (bytes / 1024.0).ToString("N1", Culture) + " КБ",
        < 1024L * 1024 * 1024 => (bytes / 1024.0 / 1024).ToString("N1", Culture) + " МБ",
        _ => (bytes / 1024.0 / 1024 / 1024).ToString("N2", Culture) + " ГБ",
    };
}
