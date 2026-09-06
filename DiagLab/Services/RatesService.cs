using System.Text.Json;

namespace DiagLab.Services;

/// <summary>
/// Получение курсов валют из внешнего источника.
/// </summary>
public sealed class RatesService
{
    private static readonly HttpClient Shared = new() { Timeout = TimeSpan.FromSeconds(10) };

    private readonly string _sourceUrl;

    public RatesService(IConfiguration config)
        => _sourceUrl = config.GetValue<string>("DiagLab:ExternalRatesUrl")
                        ?? "http://localhost:5199/api/internal/rates-source";

    /// <summary>
    /// Синхронный API для совместимости со старым кодом расчёта заказов.
    /// </summary>
    public decimal GetRate(string currency)
    {
        var json = Shared.GetStringAsync($"{_sourceUrl}?currency={currency}").Result;

        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("rate").GetDecimal();
    }

    /// <summary>
    /// Проверка доступности внешнего источника.
    /// Отдельный клиент, чтобы настройки проверки не влияли на рабочие запросы.
    /// </summary>
    public async Task<bool> PingSourceAsync()
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

        try
        {
            var response = await client.GetAsync(_sourceUrl);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }
}