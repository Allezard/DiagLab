using System.Text;

namespace LoadGen;

/// <summary>
/// Шаблон строки запроса или тела. Разбирается один раз при загрузке сценария,
/// а на каждый запрос только собирается из готовых сегментов.
/// </summary>
/// <remarks>
/// Поддерживаемые подстановки:
/// <list type="bullet">
/// <item><c>{seq}</c> — порядковый номер запроса в этом запуске: 1, 2, 3…</item>
/// <item><c>{run}</c> — короткий идентификатор запуска; вместе с <c>{seq}</c> даёт ключ, уникальный между запусками.</item>
/// <item><c>{rand:min:max}</c> — случайное целое, обе границы включительно.</item>
/// <item><c>{pick:a|b|c}</c> — случайный элемент списка.</item>
/// <item><c>{guid}</c> — новый GUID.</item>
/// <item><c>{{</c> и <c>}}</c> — литеральные фигурные скобки.</item>
/// </list>
/// </remarks>
public sealed class Template
{
    private readonly Segment[] _segments;
    private readonly bool _isConstant;
    private readonly string _source;

    private Template(string source, Segment[] segments)
    {
        _source = source;
        _segments = segments;
        _isConstant = segments.All(s => s.Kind == SegmentKind.Literal);
    }

    public override string ToString() => _source;

    public static Template Parse(string text)
    {
        var segments = new List<Segment>();
        var literal = new StringBuilder();
        var i = 0;

        while (i < text.Length)
        {
            var ch = text[i];

            if (ch == '{' && i + 1 < text.Length && text[i + 1] == '{')
            {
                literal.Append('{');
                i += 2;
                continue;
            }

            if (ch == '}' && i + 1 < text.Length && text[i + 1] == '}')
            {
                literal.Append('}');
                i += 2;
                continue;
            }

            if (ch != '{')
            {
                literal.Append(ch);
                i++;
                continue;
            }

            var end = text.IndexOf('}', i + 1);
            if (end < 0)
                throw new FormatException($"Незакрытая подстановка в шаблоне «{text}» (позиция {i}).");

            if (literal.Length > 0)
            {
                segments.Add(Segment.Literal(literal.ToString()));
                literal.Clear();
            }

            segments.Add(ParsePlaceholder(text[(i + 1)..end], text));
            i = end + 1;
        }

        if (literal.Length > 0)
            segments.Add(Segment.Literal(literal.ToString()));

        return new Template(text, [.. segments]);
    }

    public string Render(RenderContext context)
    {
        if (_isConstant)
            return _segments.Length == 0 ? string.Empty : _segments[0].Text!;

        var sb = new StringBuilder(_source.Length + 16);
        foreach (var segment in _segments)
        {
            switch (segment.Kind)
            {
                case SegmentKind.Literal:
                    sb.Append(segment.Text);
                    break;
                case SegmentKind.Seq:
                    sb.Append(context.Sequence);
                    break;
                case SegmentKind.Run:
                    sb.Append(context.RunId);
                    break;
                case SegmentKind.Random:
                    sb.Append(Random.Shared.NextInt64(segment.Min, segment.Max + 1));
                    break;
                case SegmentKind.Pick:
                    sb.Append(segment.Options![Random.Shared.Next(segment.Options.Length)]);
                    break;
                case SegmentKind.Guid:
                    sb.Append(Guid.NewGuid().ToString("N"));
                    break;
            }
        }

        return sb.ToString();
    }

    private static Segment ParsePlaceholder(string body, string source)
    {
        var name = body;
        var args = string.Empty;
        var colon = body.IndexOf(':');
        if (colon >= 0)
        {
            name = body[..colon];
            args = body[(colon + 1)..];
        }

        switch (name.Trim().ToLowerInvariant())
        {
            case "seq":
                return new Segment(SegmentKind.Seq);
            case "run":
                return new Segment(SegmentKind.Run);
            case "guid":
                return new Segment(SegmentKind.Guid);
            case "rand":
            {
                var parts = args.Split(':');
                if (parts.Length != 2
                    || !long.TryParse(parts[0], out var min)
                    || !long.TryParse(parts[1], out var max)
                    || min > max)
                {
                    throw new FormatException(
                        $"Подстановка {{{body}}} в шаблоне «{source}»: ожидается {{rand:min:max}}, где min ≤ max.");
                }

                return new Segment(SegmentKind.Random) { Min = min, Max = max };
            }
            case "pick":
            {
                var options = args.Split('|');
                if (args.Length == 0 || options.Length == 0)
                    throw new FormatException($"Подстановка {{{body}}} в шаблоне «{source}»: ожидается {{pick:a|b|c}}.");

                return new Segment(SegmentKind.Pick) { Options = options };
            }
            default:
                throw new FormatException(
                    $"Неизвестная подстановка {{{body}}} в шаблоне «{source}». " +
                    "Доступны: {seq}, {run}, {rand:min:max}, {pick:a|b}, {guid}.");
        }
    }

    private enum SegmentKind { Literal, Seq, Run, Random, Pick, Guid }

    private sealed record Segment(SegmentKind Kind)
    {
        public string? Text { get; init; }
        public long Min { get; init; }
        public long Max { get; init; }
        public string[]? Options { get; init; }

        public static Segment Literal(string text) => new(SegmentKind.Literal) { Text = text };
    }
}

public readonly record struct RenderContext(long Sequence, string RunId);
