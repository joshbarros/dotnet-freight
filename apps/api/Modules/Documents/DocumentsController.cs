using Freight.Api.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Freight.Api.Modules.Documents;

public record BillOfLadingDto(Guid BookingId, string? Number, string Status, string? Reason, DateTimeOffset CreatedAt);

[ApiController]
[Route("api/documents")]
public class DocumentsController(FreightDbContext db) : ControllerBase
{
    [HttpGet("{bookingId:guid}")]
    public async Task<ActionResult<BillOfLadingDto>> Get(Guid bookingId, CancellationToken ct)
    {
        var doc = await db.Documents.AsNoTracking().FirstOrDefaultAsync(d => d.BookingId == bookingId, ct);
        return doc is null
            ? NotFound()
            : new BillOfLadingDto(doc.BookingId, doc.Number, doc.Status.ToString(), doc.Reason, doc.CreatedAt);
    }
}
