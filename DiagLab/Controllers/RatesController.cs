using DiagLab.Services;
using Microsoft.AspNetCore.Mvc;

namespace DiagLab.Controllers;

[ApiController]
[Route("api/rates")]
public sealed class RatesController : ControllerBase
{
    private readonly RatesService _rates;

    public RatesController(RatesService rates) => _rates = rates;

    [HttpGet]
    public IActionResult Get([FromQuery] string? currency)
    {
        var code = (currency ?? "EUR").ToUpperInvariant();
        var rate = _rates.GetRate(code);

        return Ok(new { currency = code, rate, at = DateTime.UtcNow });
    }

    [HttpGet("ping")]
    public async Task<IActionResult> Ping()
    {
        var ok = await _rates.PingSourceAsync();
        return Ok(new { sourceAvailable = ok });
    }
}