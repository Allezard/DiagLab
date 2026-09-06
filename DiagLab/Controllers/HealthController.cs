using Microsoft.AspNetCore.Mvc;
using System.Diagnostics;

namespace DiagLab.Controllers;

[ApiController]
[Route("api/health")]
public sealed class HealthController : ControllerBase
{
    [HttpGet]
    public IActionResult Get()
    {
        using var process = Process.GetCurrentProcess();

        var uptime = DateTime.UtcNow - process.StartTime.ToUniversalTime();

        return Ok(new
        {
            status = "ok",
            uptime = Format(uptime),
            at = DateTime.UtcNow
        });
    }

    /// <summary>
    /// Дни показываем отдельно: сервис живёт дольше суток,
    /// и формат hh:mm:ss их бы потерял.
    /// </summary>
    private static string Format(TimeSpan span)
        => span.TotalDays >= 1
            ? span.ToString(@"d\.hh\:mm\:ss")
            : span.ToString(@"hh\:mm\:ss");
}