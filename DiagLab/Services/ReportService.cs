namespace DiagLab.Services;

/// <summary>
/// Формирование выгрузок. Последние сформированные отчёты держим в памяти,
/// чтобы повторный запрос за тем же файлом не пересобирал его заново.
/// </summary>
public sealed class ReportService
{
    private const int RecentLimit = 40;

    private static readonly List<byte[]> RecentExports = new(RecentLimit);
    private static readonly object RecentGate = new();

    /// <summary>
    /// Бинарная выгрузка: 128 байт на строку.
    /// </summary>
    public byte[] ExportBinary(int rows)
    {
        var buffer = new byte[rows * 128];

        for (var i = 0; i < buffer.Length; i += 128)
        {
            buffer[i] = (byte)(i % 251);
        }

        lock (RecentGate)
        {
            RecentExports.Add(buffer);
            if (RecentExports.Count > RecentLimit)
            {
                RecentExports.RemoveAt(0);
            }
        }

        return buffer;
    }

    /// <summary>
    /// Текстовая выгрузка в CSV.
    /// </summary>
    public string ExportCsv(int rows)
    {
        var csv = "Id;Sku;Name;Price\n";

        for (var i = 1; i <= rows; i++)
        {
            csv += i + ";SKU-" + i.ToString("D6") + ";Позиция " + i + ";" + (i * 13.7m).ToString("F2") + "\n";
        }

        return csv;
    }

    /// <summary>
    /// Служебный метод: сколько выгрузок держим и какого суммарного размера.
    /// </summary>
    public (int Count, long Bytes) RecentStats()
    {
        lock (RecentGate)
        {
            return (RecentExports.Count, RecentExports.Sum(b => (long)b.Length));
        }
    }
}