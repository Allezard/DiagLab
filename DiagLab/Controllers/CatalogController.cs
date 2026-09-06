using DiagLab.Services;
using Microsoft.AspNetCore.Mvc;

namespace DiagLab.Controllers;

[ApiController]
[Route("api/catalog")]
public sealed class CatalogController : ControllerBase
{
    private readonly CatalogService _catalog;

    public CatalogController(CatalogService catalog) => _catalog = catalog;

    [HttpGet("search")]
    public IActionResult Search([FromQuery] string? q)
    {
        var query = string.IsNullOrWhiteSpace(q) ? "все товары" : q;
        var result = _catalog.Search(query);

        return Ok(new
        {
            query = result.Query,
            builtAt = result.BuiltAt,
            found = result.Items.Count,
            sample = result.Items.Take(3).Select(p => new { p.Sku, p.Name, p.Price })
        });
    }
}