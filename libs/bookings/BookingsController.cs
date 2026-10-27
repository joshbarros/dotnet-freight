using System.Text.Json;
using Freight.Shared.Auth;
using Freight.Shared.Messaging;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Freight.Bookings;

[ApiController]
[Route("api/bookings")]
[Authorize]
public class BookingsController(BookingService service, KafkaLog kafka) : ControllerBase
{
    /// <summary>Accepts the booking for the signed-in user. Capacity and the bill of lading follow asynchronously; poll GET to see them.</summary>
    [HttpPost]
    public async Task<ActionResult<BookingDto>> Create(CreateBookingRequest request, CancellationToken ct)
    {
        var booking = await service.CreateAsync(request, ct);
        return Accepted($"/api/bookings/{booking.Id}", BookingDto.From(booking));
    }

    /// <summary>Your bookings, newest first, a page at a time (default 100, at most 100). Staff see everyone's. The total is in X-Total-Count.</summary>
    [HttpGet]
    public async Task<IEnumerable<BookingDto>> List([FromQuery] string? sailingCode, [FromQuery] int page = 1, [FromQuery] int pageSize = 100, CancellationToken ct = default)
    {
        var (items, total) = await service.ListAsync(sailingCode, Math.Max(page, 1), Math.Clamp(pageSize, 1, 100), ct);
        Response.Headers["X-Total-Count"] = total.ToString();
        return items.Select(BookingDto.From);
    }

    [HttpGet("{id:guid}")]
    public async Task<BookingDto> Get(Guid id, CancellationToken ct) => BookingDto.From(await service.GetAsync(id, ct));

    [HttpPost("{id:guid}/reversal")]
    public async Task<ActionResult<BookingDto>> Reverse(Guid id, CancellationToken ct) =>
        Accepted($"/api/bookings/{id}", BookingDto.From(await service.ReverseAsync(id, ct)));

    /// <summary>The Kafka event log for one booking, read from the start of the topic. Staff only.</summary>
    [HttpGet("{id:guid}/events")]
    [Authorize(Policy = Policies.ViewAll)]
    public async Task<IEnumerable<JsonElement>> Events(Guid id, CancellationToken ct) =>
        (await Task.Run(() => kafka.Read(id.ToString(), TimeSpan.FromSeconds(10)), ct))
            .Select(v => JsonDocument.Parse(v).RootElement.Clone());
}
