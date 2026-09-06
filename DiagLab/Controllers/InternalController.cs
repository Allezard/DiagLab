using Microsoft.AspNetCore.Mvc;

namespace DiagLab.Controllers;

/// <summary>
/// Имитация внешнего источника курсов, чтобы стенд работал без интернета.
/// </summary>
[ApiController]
[Route("api/internal")]
public sealed class InternalController : ControllerBase
{
    [HttpGet("rates-source")]
    public async Task<IActionResult> RatesSource([FromQuery] string? currency)
    {
        await Task.Delay(120);

        var code = (currency ?? "EUR").ToUpperInvariant();

        var rate = code switch
        {
            "USD" => 1.08m,
            "EUR" => 1.00m,
            "GBP" => 0.85m,
            _ => Math.Round((decimal)(Random.Shared.NextDouble() * 100), 4)
        };

        return Ok(new { currency = code, rate });
    }
}