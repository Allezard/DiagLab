using DiagLab.Services;
using Microsoft.AspNetCore.Mvc;

namespace DiagLab.Controllers;

[ApiController]
[Route("api/inventory")]
public sealed class InventoryController : ControllerBase
{
    private readonly InventoryService _inventory;

    public InventoryController(InventoryService inventory) => _inventory = inventory;

    [HttpGet("reserve")]
    public IActionResult Reserve([FromQuery] int? product, [FromQuery] int? qty)
    {
        var id = product ?? Random.Shared.Next(1, 501);
        var quantity = qty ?? 1;

        var ok = _inventory.Reserve(id, quantity);

        return Ok(new
        {
            product = id,
            quantity,
            reserved = ok,
            left = _inventory.Available(id)
        });
    }
}