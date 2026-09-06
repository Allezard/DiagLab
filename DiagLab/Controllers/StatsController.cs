using DiagLab.Services;
using Microsoft.AspNetCore.Mvc;

namespace DiagLab.Controllers;

/// <summary>
/// Внутреннее состояние сервиса. Нужен для сверки выводов, сделанных
/// инструментами диагностики. В настоящем приложении такой ручки не будет.
/// </summary>
[ApiController]
[Route("api/stats")]
public sealed class StatsController : ControllerBase
{
    private readonly CatalogService _catalog;
    private readonly ReportService _reports;
    private readonly SessionService _sessions;

    public StatsController(CatalogService catalog, ReportService reports, SessionService sessions)
    {
        _catalog = catalog;
        _reports = reports;
        _sessions = sessions;
    }

    [HttpGet]
    public IActionResult Get()
    {
        var recent = _reports.RecentStats();

        return Ok(new
        {
            catalogCacheEntries = _catalog.CacheSize,
            recentReports = recent.Count,
            recentReportBytes = recent.Bytes,
            priceFeedSubscribers = PriceFeed.SubscriberCount,
            activeSessions = _sessions.ActiveCount,
            managedHeapBytes = GC.GetTotalMemory(false),
            gen0 = GC.CollectionCount(0),
            gen1 = GC.CollectionCount(1),
            gen2 = GC.CollectionCount(2)
        });
    }
}