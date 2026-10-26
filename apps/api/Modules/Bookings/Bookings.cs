using Freight.Api.Data;
using Freight.Api.Messaging;
using Freight.Api.Modules.Capacity;
using Freight.Api.Modules.Documents;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Freight.Api.Modules.Bookings;

public enum BookingStatus { Requested, Held, Issued, Reversed, Rejected }

public class Booking
{
    public Guid Id { get; set; }
    public Guid SailingId { get; set; }
    public string SailingCode { get; set; } = "";
    public string Reference { get; set; } = "";
    public int Teu { get; set; }
    public long FeeCents { get; set; }
    public BookingStatus Status { get; set; }
    public string? Reason { get; set; }
    public string? BillOfLadingNumber { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? CapacityReleasedAt { get; set; }
}

public class BookingOptions
{
    /// <summary>Latency knob for the final mark. Compose sets it so the reversal race window is wide enough to hit on purpose.</summary>
    public int FinalMarkDelayMs { get; set; }
}

public class DomainException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

public record CreateBookingRequest(string? SailingCode, string? Reference, long Teu);

public record BookingDto(
    Guid Id, string SailingCode, string Reference, int Teu, long FeeCents, string Status,
    string? Reason, string? BillOfLadingNumber, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    DateTimeOffset? CapacityReleasedAt)
{
    public static BookingDto From(Booking b) => new(
        b.Id, b.SailingCode, b.Reference, b.Teu, b.FeeCents, b.Status.ToString(),
        b.Reason, b.BillOfLadingNumber, b.CreatedAt, b.UpdatedAt, b.CapacityReleasedAt);
}

/// <summary>HTTP side of the saga: writes the booking and its first outbox row in one local transaction.</summary>
public class BookingService(FreightDbContext db)
{
    public async Task<Booking> CreateAsync(CreateBookingRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.SailingCode))
            throw new DomainException(400, "Sailing code is required.");
        var error = BookingRules.Validate(request.Reference, request.Teu);
        if (error is not null)
            throw new DomainException(400, error);

        var sailing = await db.Sailings.AsNoTracking().FirstOrDefaultAsync(s => s.Code == request.SailingCode.Trim(), ct)
            ?? throw new DomainException(404, $"Sailing '{request.SailingCode}' does not exist.");

        var teu = (int)request.Teu;
        var reference = request.Reference!.Trim();
        var now = DateTimeOffset.UtcNow;
        var booking = new Booking
        {
            Id = Guid.CreateVersion7(),
            SailingId = sailing.Id,
            SailingCode = sailing.Code,
            Reference = reference,
            Teu = teu,
            FeeCents = BookingRules.FeeCents(teu),
            Status = BookingStatus.Requested,
            CreatedAt = now,
            UpdatedAt = now,
        };

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.Bookings.Add(booking);
        db.Enqueue("bookings", Topology.ReserveKey, booking.Id, new ReserveCapacity(booking.Id, sailing.Id, teu, reference));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw new DomainException(409, $"Reference '{reference}' is already booked on {sailing.Code}.");
        }
        await tx.CommitAsync(ct);
        return booking;
    }

    /// <summary>Moves Held or Issued to Reversed and asks Capacity to give the unit back. The bill of lading is not touched.</summary>
    public async Task<Booking> ReverseAsync(Guid id, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var moved = await db.Bookings
            .Where(b => b.Id == id && (b.Status == BookingStatus.Held || b.Status == BookingStatus.Issued))
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.Status, BookingStatus.Reversed)
                .SetProperty(b => b.Reason, "Reversed by request.")
                .SetProperty(b => b.UpdatedAt, now), ct);
        if (moved != 1)
        {
            var current = await db.Bookings.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id, ct)
                ?? throw new DomainException(404, "Booking not found.");
            throw new DomainException(409, $"A booking in status {current.Status} cannot be reversed.");
        }

        db.Enqueue("bookings", Topology.ReleaseKey, id, new ReleaseCapacity(id, "reversal"));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await db.Bookings.AsNoTracking().FirstAsync(b => b.Id == id, ct);
    }
}

public enum MarkOutcome { Marked, Skipped, Duplicate }

/// <summary>Consumes the events that move a booking forward. Each step runs only from the status it expects.</summary>
public class BookingsHandler(FreightDbContext db, IOptions<BookingOptions> options) : IMessageHandler
{
    public const string Name = "bookings";

    public Task HandleAsync(Delivery d, CancellationToken ct) => d.RoutingKey switch
    {
        Topology.HeldKey => MarkHeldAsync(d.MessageId, Json.Parse<CapacityHeld>(d.Body).BookingId, ct),
        Topology.ShortKey => MarkShortAsync(d.MessageId, Json.Parse<CapacityShort>(d.Body), ct),
        Topology.IssuedKey => MarkIssuedAsync(d.MessageId, Json.Parse<BillOfLadingIssued>(d.Body), ct),
        Topology.RejectedKey => MarkRejectedAsync(d.MessageId, Json.Parse<DocumentRejected>(d.Body), ct),
        Topology.ReleasedKey => MarkReleasedAsync(d.MessageId, Json.Parse<CapacityReleased>(d.Body), ct),
        _ => throw new InvalidOperationException($"Bookings does not handle '{d.RoutingKey}'."),
    };

    public Task<MarkOutcome> MarkHeldAsync(Guid messageId, Guid bookingId, CancellationToken ct = default) =>
        StepAsync(messageId, ct, () => db.Bookings
            .Where(b => b.Id == bookingId && b.Status == BookingStatus.Requested)
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.Status, BookingStatus.Held)
                .SetProperty(b => b.UpdatedAt, DateTimeOffset.UtcNow), ct));

    public Task<MarkOutcome> MarkShortAsync(Guid messageId, CapacityShort m, CancellationToken ct = default) =>
        StepAsync(messageId, ct, () => db.Bookings
            .Where(b => b.Id == m.BookingId && b.Status == BookingStatus.Requested)
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.Status, BookingStatus.Rejected)
                .SetProperty(b => b.Reason, $"Short: {m.RequestedTeu} TEU requested, {m.RemainingTeu} remaining.")
                .SetProperty(b => b.UpdatedAt, DateTimeOffset.UtcNow), ct));

    /// <summary>The final mark. If a reversal got there first the row is no longer Held and nothing changes.</summary>
    public async Task<MarkOutcome> MarkIssuedAsync(Guid messageId, BillOfLadingIssued m, CancellationToken ct = default)
    {
        if (options.Value.FinalMarkDelayMs > 0)
            await Task.Delay(options.Value.FinalMarkDelayMs, ct);

        return await StepAsync(messageId, ct, () => db.Bookings
            .Where(b => b.Id == m.BookingId && b.Status == BookingStatus.Held)
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.Status, BookingStatus.Issued)
                .SetProperty(b => b.BillOfLadingNumber, m.Number)
                .SetProperty(b => b.UpdatedAt, DateTimeOffset.UtcNow), ct),
            bookingIdForOrderCheck: m.BookingId);
    }

    public Task<MarkOutcome> MarkRejectedAsync(Guid messageId, DocumentRejected m, CancellationToken ct = default) =>
        StepAsync(messageId, ct, () => db.Bookings
            .Where(b => b.Id == m.BookingId && (b.Status == BookingStatus.Held || b.Status == BookingStatus.Requested))
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.Status, BookingStatus.Rejected)
                .SetProperty(b => b.Reason, m.Reason)
                .SetProperty(b => b.UpdatedAt, DateTimeOffset.UtcNow), ct));

    public Task<MarkOutcome> MarkReleasedAsync(Guid messageId, CapacityReleased m, CancellationToken ct = default) =>
        StepAsync(messageId, ct, () => db.Bookings
            .Where(b => b.Id == m.BookingId && b.CapacityReleasedAt == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.CapacityReleasedAt, DateTimeOffset.UtcNow), ct));

    private async Task<MarkOutcome> StepAsync(Guid messageId, CancellationToken ct, Func<Task<int>> update, Guid? bookingIdForOrderCheck = null)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await db.ClaimAsync(Name, messageId, ct))
        {
            await tx.CommitAsync(ct);
            return MarkOutcome.Duplicate;
        }

        var changed = await update();
        if (changed == 0 && bookingIdForOrderCheck is { } id)
        {
            // Skipped for a reason we can name? Requested means the "Held" mark has not landed yet: retry, do not drop.
            var status = await db.Bookings.AsNoTracking().Where(b => b.Id == id).Select(b => b.Status).FirstOrDefaultAsync(ct);
            if (status == BookingStatus.Requested)
                throw new OutOfOrderException("Final mark arrived before the Held mark.");
        }

        await tx.CommitAsync(ct);
        return changed == 1 ? MarkOutcome.Marked : MarkOutcome.Skipped;
    }
}
