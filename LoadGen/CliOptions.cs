using System.Globalization;

namespace LoadGen;

public sealed class CliOptions
{
    public string? Scenario { get; private set; }
    public TimeSpan? Duration { get; private set; }
    public long? Requests { get; private set; }
    public int Concurrency { get; private set; } = 8;
    public bool ConcurrencySet { get; private set; }
    public double Rps { get; private set; }
    public int MaxInFlight { get; private set; } = 1000;
    public string? BaseUrl { get; private set; }
    public string? Url { get; private set; }
    public string Method { get; private set; } = "GET";
    public string? Body { get; private set; }
    public string ContentType { get; private set; } = "application/json";
    public TimeSpan Delay { get; private set; }
    public TimeSpan Timeout { get; private set; } = TimeSpan.FromSeconds(30);
    public TimeSpan Warmup { get; private set; }
    public TimeSpan Interval { get; private set; } = TimeSpan.FromSeconds(1);
    public string? ConfigPath { get; private set; }
    public string? JsonPath { get; private set; }
    public bool NoHealthCheck { get; private set; }
    public bool List { get; private set; }
    public bool Help { get; private set; }

    public static CliOptions Parse(string[] args)
    {
        var o = new CliOptions();

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            string? inline = null;

            if (arg.StartsWith("--", StringComparison.Ordinal) && arg.Contains('='))
            {
                var eq = arg.IndexOf('=');
                inline = arg[(eq + 1)..];
                arg = arg[..eq];
            }

            string Next()
            {
                if (inline is not null) return inline;
                if (i + 1 >= args.Length || (args[i + 1].StartsWith('-') && args[i + 1].Length > 1 && !char.IsDigit(args[i + 1][1])))
                    throw new ArgumentException($"после {arg} ожидается значение");
                return args[++i];
            }

            switch (arg)
            {
                case "-h" or "--help" or "-?" or "/?":
                    o.Help = true;
                    break;
                case "-l" or "--list":
                    o.List = true;
                    break;
                case "-s" or "--scenario":
                    o.Scenario = Next();
                    break;
                case "-d" or "--duration":
                    o.Duration = ParseDuration(Next(), arg);
                    break;
                case "-n" or "--requests":
                    o.Requests = ParsePositiveLong(Next(), arg);
                    break;
                case "-c" or "--concurrency":
                    o.Concurrency = (int)ParsePositiveLong(Next(), arg);
                    o.ConcurrencySet = true;
                    break;
                case "-r" or "--rps":
                    o.Rps = ParsePositiveDouble(Next(), arg);
                    break;
                case "--max-inflight":
                    o.MaxInFlight = (int)ParsePositiveLong(Next(), arg);
                    break;
                case "-u" or "--base-url":
                    o.BaseUrl = Next();
                    break;
                case "--url":
                    o.Url = Next();
                    break;
                case "-m" or "--method":
                    o.Method = Next();
                    break;
                case "--body":
                    o.Body = Next();
                    break;
                case "--content-type":
                    o.ContentType = Next();
                    break;
                case "--delay":
                    o.Delay = ParseDuration(Next(), arg, defaultUnitMs: true);
                    break;
                case "--timeout":
                    o.Timeout = ParseDuration(Next(), arg);
                    break;
                case "--warmup":
                    o.Warmup = ParseDuration(Next(), arg);
                    break;
                case "--interval":
                    o.Interval = ParseDuration(Next(), arg, allowZero: true);
                    break;
                case "--config":
                    o.ConfigPath = Next();
                    break;
                case "--json":
                    o.JsonPath = Next();
                    break;
                case "--no-health":
                    o.NoHealthCheck = true;
                    break;
                default:
                    if (arg.StartsWith('-') && arg.Length > 1)
                        throw new ArgumentException($"неизвестный ключ {arg}");
                    if (o.Scenario is not null)
                        throw new ArgumentException($"сценарий уже задан («{o.Scenario}»), лишний аргумент «{arg}»");
                    o.Scenario = arg;
                    break;
            }
        }

        if (o.Rps > 0 && o.ConcurrencySet)
            throw new ArgumentException("-c задаёт закрытую модель, --rps — открытую; укажите что-то одно");

        return o;
    }

    /// <summary>90 → 90 с; также 500ms, 30s, 2m, 1h, 00:01:30.</summary>
    public static TimeSpan ParseDuration(string text, string option, bool defaultUnitMs = false, bool allowZero = false)
    {
        text = text.Trim().ToLowerInvariant();
        TimeSpan value;

        if (text.Contains(':'))
        {
            if (!TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out value))
                throw new ArgumentException($"{option}: не удалось разобрать «{text}»");
        }
        else
        {
            var (number, unit) = SplitUnit(text);
            if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) || n < 0)
                throw new ArgumentException($"{option}: не удалось разобрать «{text}»");

            value = unit switch
            {
                "" => defaultUnitMs ? TimeSpan.FromMilliseconds(n) : TimeSpan.FromSeconds(n),
                "ms" or "мс" => TimeSpan.FromMilliseconds(n),
                "s" or "с" or "sec" => TimeSpan.FromSeconds(n),
                "m" or "м" or "min" => TimeSpan.FromMinutes(n),
                "h" or "ч" => TimeSpan.FromHours(n),
                _ => throw new ArgumentException($"{option}: неизвестная единица «{unit}» (ms, s, m, h)"),
            };
        }

        if (value == TimeSpan.Zero && !allowZero && !defaultUnitMs)
            throw new ArgumentException($"{option}: значение должно быть больше нуля");

        return value;
    }

    private static (string Number, string Unit) SplitUnit(string text)
    {
        var i = 0;
        while (i < text.Length && (char.IsDigit(text[i]) || text[i] is '.' or ','))
            i++;
        return (text[..i].Replace(',', '.'), text[i..].Trim());
    }

    private static long ParsePositiveLong(string text, string option) =>
        long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0
            ? n
            : throw new ArgumentException($"{option}: ожидается положительное целое, получено «{text}»");

    private static double ParsePositiveDouble(string text, string option) =>
        double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && n > 0
            ? n
            : throw new ArgumentException($"{option}: ожидается положительное число, получено «{text}»");

    public static void PrintHelp()
    {
        Console.WriteLine("""
            LoadGen — генератор HTTP-нагрузки для демо-стендов курсов.

            Использование:
              loadgen [сценарий] [параметры]
              loadgen --url <адрес или путь> [параметры]

            Что нагружать:
              -s, --scenario <имя>   сценарий из loadgen.json (можно просто первым словом)
                  --url <шаблон>     разовый запрос без файла сценариев
              -m, --method <метод>   HTTP-метод для --url (GET)
                  --body <шаблон>    тело запроса для --url
              -l, --list             показать сценарии

            Сколько:
              -d, --duration <время> длительность: 90, 30s, 2m (по умолчанию 60 с)
              -n, --requests <N>     ровно N запросов; без -d — до их завершения
                  --warmup <время>   прогрев: нагрузка идёт, в итог не попадает

            Как:
              -c, --concurrency <N>  закрытая модель: N клиентов, каждый ждёт ответа (8)
              -r, --rps <N>          открытая модель: N запросов в секунду, ответы не ждём
                  --max-inflight <N> предел одновременных запросов в открытой модели (1000)
                  --delay <мс>       пауза клиента между запросами (закрытая модель)
                  --timeout <время>  таймаут одного запроса (30s)

            Прочее:
              -u, --base-url <url>   адрес сервиса (по умолчанию из loadgen.json)
                  --config <файл>    файл сценариев (ищется loadgen.json в текущем каталоге,
                                     затем рядом с программой)
                  --interval <время> период живой статистики (1s; 0 — выключить)
                  --json <файл>      сохранить итог в JSON
                  --no-health        не проверять доступность сервиса перед стартом

            Подстановки в шаблонах:
              {seq}           номер запроса в запуске: 1, 2, 3…
              {run}           идентификатор запуска (уникален между запусками)
              {rand:1:500}    случайное целое, границы включительно
              {pick:EUR|USD}  случайный элемент списка
              {guid}          новый GUID

            Примеры:
              loadgen search -d 120 -c 8
              loadgen search -n 2000 -c 8
              loadgen rates --rps 150 -d 90
              loadgen --url "/api/reports/export?rows={rand:700:9000}" -c 4 -d 30
            """);
    }
}
