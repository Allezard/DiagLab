using DiagLab.Services;
using Microsoft.AspNetCore.Mvc;

namespace DiagLab.Controllers;

[ApiController]
[Route("api/sessions")]
public sealed class SessionsController : ControllerBase
{
    private readonly SessionService _sessions;

    public SessionsController(SessionService sessions) => _sessions = sessions;

    [HttpGet("open")]
    public IActionResult Open([FromQuery] string? user)
    {
        var session = _sessions.Open(user ?? $"user-{Random.Shared.Next(100_000)}");

        return Ok(new
        {
            session.SessionId,
            session.UserId,
            active = _sessions.ActiveCount
        });
    }

    [HttpGet("close")]
    public IActionResult Close([FromQuery] string sessionId)
        => Ok(new { closed = _sessions.Close(sessionId), active = _sessions.ActiveCount });
}