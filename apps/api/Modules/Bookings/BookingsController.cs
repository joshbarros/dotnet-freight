using System.Text.Json;
using Freight.Api.Data;
using Freight.Api.Messaging;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Freight.Api.Modules.Bookings;

[ApiController]
[Route("api/bookings")]
public class BookingsController(FreightDbContext db, BookingService service, KafkaLog kafka) : ControllerBase
{
    /// <summary>Accepts the booking. Capacity and the bill of lading follow asynchronously; poll GET to see them.</summary>
    [HttpPost]
    public async Task<ActionResult<BookingDto>> Create(CreateBookingRequest request, CancellationToken ct)
    {
        var booking = await service.CreateAsync(request, ct);
        return Accepted($"/api/bookings/{booking.Id}", BookingDto.From(booking));
    }

    [HttpGet]
    public async Task<IEnumerable<BookingDto>> List([FromQuery] string? sailingCode, CancellationToken ct)
    {
        var query = db.Bookings.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(sailingCode))
            query = query.Where(b => b.SailingCode == sailingCode);
        return (await query.OrderByDescending(b => b.CreatedAt).Take(100).ToListAsync(ct)).Select(BookingDto.From);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<BookingDto>> Get(Guid id, CancellationToken ct)
    {
        var booking = await db.Bookings.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id, ct);
        return booking is null ? NotFound() : BookingDto.From(booking);
    }

    [HttpPost("{id:guid}/reversal")]
    public async Task<ActionResult<BookingDto>> Reverse(Guid id, CancellationToken ct) =>
        Accepted($"/api/bookings/{id}", BookingDto.From(await service.ReverseAsync(id, ct)));

    /// <summary>The Kafka event log for one booking, read from the start of the topic.</summary>
    [HttpGet("{id:guid}/events")]
    public async Task<IEnumerable<JsonElement>> Events(Guid id, CancellationToken ct) =>
        (await Task.Run(() => kafka.Read(id.ToString(), TimeSpan.FromSeconds(10)), ct))
            .Select(v => JsonDocument.Parse(v).RootElement.Clone());
}
