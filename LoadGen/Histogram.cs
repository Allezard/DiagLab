using System.Numerics;

namespace LoadGen;

/// <summary>
/// Гистограмма задержек в микросекундах с логарифмическими корзинами (точность ~1,5%).
/// Запись — один <see cref="Interlocked.Increment(ref long)"/>, без блокировок и без аллокаций:
/// генератор нагрузки не должен сам создавать ту конкуренцию, которую курс учит находить.
/// </summary>
public sealed class Histogram
{
    private const int SubBits = 6;
    private const int SubCount = 1 << SubBits;          // 64 корзины на каждую степень двойки
    private const int MaxShift = 40;                     // до ~2^46 мкс — с огромным запасом

    private readonly long[] _counts = new long[SubCount * (MaxShift + 2)];
    private long _total;
    private long _max;

    public long Count => Interlocked.Read(ref _total);
    public long MaxMicros => Interlocked.Read(ref _max);

    public void Record(long micros)
    {
        if (micros < 0) micros = 0;

        Interlocked.Increment(ref _counts[IndexOf(micros)]);
        Interlocked.Increment(ref _total);

        var current = Interlocked.Read(ref _max);
        while (micros > current)
        {
            var seen = Interlocked.CompareExchange(ref _max, micros, current);
            if (seen == current) break;
            current = seen;
        }
    }

    /// <summary>Перцентиль в микросекундах. <paramref name="percentile"/> — от 0 до 100.</summary>
    public long Percentile(double percentile)
    {
        var total = Count;
        if (total == 0) return 0;

        var rank = (long)Math.Ceiling(percentile / 100.0 * total);
        if (rank < 1) rank = 1;

        long seen = 0;
        for (var i = 0; i < _counts.Length; i++)
        {
            seen += Interlocked.Read(ref _counts[i]);
            if (seen >= rank)
                return Math.Min(ValueAt(i), MaxMicros);
        }

        return MaxMicros;
    }

    private static int IndexOf(long value)
    {
        if (value < SubCount)
            return (int)value;

        var msb = 63 - BitOperations.LeadingZeroCount((ulong)value);
        var shift = Math.Min(msb - SubBits, MaxShift);
        var top = (int)(value >> shift);                 // в диапазоне [64, 128)
        if (top >= 2 * SubCount) top = 2 * SubCount - 1; // только при переполнении MaxShift
        return SubCount * (shift + 1) + (top - SubCount);
    }

    private static long ValueAt(int index)
    {
        if (index < SubCount)
            return index;

        var shift = index / SubCount - 1;
        var top = index % SubCount + SubCount;
        // середина корзины
        return ((long)top << shift) + ((1L << shift) >> 1);
    }
}
