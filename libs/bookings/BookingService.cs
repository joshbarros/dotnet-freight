using Freight.Shared;
using Freight.Shared.Auth;
using Freight.Shared.Contracts;
using Freight.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Freight.Bookings;

/// <summary>HTTP side of the saga: writes the booking and its first outbox row in one local transaction.</summary>
public class BookingService(BookingsDbContext db, ISailingCatalog sailings, ICurrentUser user)
{
    public async Task<Booking> CreateAsync(CreateBookingRequest request, CancellationToken ct)
    {
        var ownerId = user.Id ?? throw new DomainException(401, "Sign in to book.");
        if (string.IsNullOrWhiteSpace(request.SailingCode))
            throw new DomainException(400, "Sailing code is required.");
        var error = BookingRules.Validate(request.Reference, request.Teu);
        if (error is not null)
            throw new DomainException(400, error);

        var sailing = await sailings.FindAsync(request.SailingCode.Trim(), ct)
            ?? throw new DomainException(404, $"Sailing '{request.SailingCode}' does not exist.");

        var teu = (int)request.Teu;
        var reference = request.Reference!.Trim();
        var now = DateTimeOffset.UtcNow;
        var booking = new Booking
        {
            Id = Guid.CreateVersion7(),
            OwnerId = ownerId,
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
        db.Enqueue("bookings", Topology.ReserveKey, booking.Id, new ReserveCapacity(booking.Id, ownerId, sailing.Id, teu, reference));
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

    /// <summary>The owner or staff only. Anyone else gets a 404, so ids cannot be probed.</summary>
    public async Task<Booking> GetAsync(Guid id, CancellationToken ct)
    {
        var booking = await db.Bookings.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id, ct);
        if (booking is null || !OwnerAccess.CanAccess(user, booking.OwnerId))
            throw new DomainException(404, "Booking not found.");
        return booking;
    }

    public async Task<List<Booking>> ListAsync(string? sailingCode, CancellationToken ct)
    {
        var query = db.Bookings.AsNoTracking();
        if (!user.IsStaff)
        {
            var me = user.Id ?? throw new DomainException(401, "Sign in to see bookings.");
            query = query.Where(b => b.OwnerId == me);
        }
        if (!string.IsNullOrWhiteSpace(sailingCode))
            query = query.Where(b => b.SailingCode == sailingCode);
        return await query.OrderByDescending(b => b.CreatedAt).Take(100).ToListAsync(ct);
    }

    /// <summary>Moves Held or Issued to Reversed and asks Capacity to give the unit back. The bill of lading is not touched.</summary>
    public async Task<Booking> ReverseAsync(Guid id, CancellationToken ct)
    {
        await GetAsync(id, ct); // 404 unless the caller may see it

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
            var current = await db.Bookings.AsNoTracking().FirstAsync(b => b.Id == id, ct);
            throw new DomainException(409, $"A booking in status {current.Status} cannot be reversed.");
        }

        db.Enqueue("bookings", Topology.ReleaseKey, id, new ReleaseCapacity(id, "reversal"));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await db.Bookings.AsNoTracking().FirstAsync(b => b.Id == id, ct);
    }
}
