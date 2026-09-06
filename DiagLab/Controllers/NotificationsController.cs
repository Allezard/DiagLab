using DiagLab.Services;
using Microsoft.AspNetCore.Mvc;

namespace DiagLab.Controllers;

[ApiController]
[Route("api/notifications")]
public sealed class NotificationsController : ControllerBase
{
    [HttpGet("subscribe")]
    public IActionResult Subscribe([FromQuery] string? user)
    {
        var watcher = new PriceWatcher(user ?? $"user-{Random.Shared.Next(100_000)}");

        return Ok(new
        {
            user = watcher.UserId,
            createdAt = watcher.CreatedAt,
            subscribers = PriceFeed.SubscriberCount
        });
    }
}