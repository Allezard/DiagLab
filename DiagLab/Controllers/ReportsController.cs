using DiagLab.Services;
using Microsoft.AspNetCore.Mvc;

namespace DiagLab.Controllers;

[ApiController]
[Route("api/reports")]
public sealed class ReportsController : ControllerBase
{
    private readonly ReportService _reports;

    public ReportsController(ReportService reports) => _reports = reports;

    [HttpGet("export")]
    public IActionResult Export([FromQuery] int? rows)
    {
        var count = Math.Clamp(rows ?? 1000, 1, 100_000);
        var data = _reports.ExportBinary(count);
        var stats = _reports.RecentStats();

        return Ok(new
        {
            rows = count,
            bytes = data.Length,
            cachedReports = stats.Count,
            cachedBytes = stats.Bytes
        });
    }

    [HttpGet("csv")]
    public IActionResult Csv([FromQuery] int? rows)
    {
        var count = Math.Clamp(rows ?? 5_000, 1, 200_000);
        var csv = _reports.ExportCsv(count);

        return Ok(new { rows = count, characters = csv.Length });
    }
}