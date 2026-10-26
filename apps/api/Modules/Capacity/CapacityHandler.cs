using Freight.Api.Data;
using Freight.Api.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Freight.Api.Modules.Capacity;

public enum ReserveOutcome { Held, Short, Duplicate }

/// <summary>
/// The only writer of Sailing.RemainingTeu and of capacity holds.
/// The reservation is one conditional UPDATE: it matches only while the remaining TEU still covers the request.
/// </summary>
public class CapacityHandler(FreightDbContext db) : IMessageHandler
{
    public const string Name = "capacity";

    public Task HandleAsync(Delivery delivery, CancellationToken ct) => delivery.RoutingKey switch
    {
        Topology.ReserveKey => ReserveAsync(delivery.MessageId, Json.Parse<ReserveCapacity>(delivery.Body), ct),
        Topology.ReleaseKey => ReleaseAsync(delivery.MessageId, Json.Parse<ReleaseCapacity>(delivery.Body), ct),
        _ => throw new InvalidOperationException($"Capacity does not handle '{delivery.RoutingKey}'."),
    };

    public async Task<ReserveOutcome> ReserveAsync(Guid messageId, ReserveCapacity command, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await db.ClaimAsync(Name, messageId, ct))
        {
            await tx.CommitAsync(ct);
            return ReserveOutcome.Duplicate;
        }

        var updated = await db.Sailings
            .Where(s => s.Id == command.SailingId && s.RemainingTeu >= command.Teu)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RemainingTeu, x => x.RemainingTeu - command.Teu), ct);

        ReserveOutcome outcome;
        if (updated == 1)
        {
            db.Holds.Add(new CapacityHold
            {
                BookingId = command.BookingId,
                SailingId = command.SailingId,
                Teu = command.Teu,
                Status = HoldStatus.Held,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            db.Enqueue("capacity", Topology.HeldKey, command.BookingId,
                new CapacityHeld(command.BookingId, command.SailingId, command.Teu, command.Reference));
            outcome = ReserveOutcome.Held;
        }
        else
        {
            var remaining = await db.Sailings.Where(s => s.Id == command.SailingId)
                .Select(s => s.RemainingTeu).FirstOrDefaultAsync(ct);
            db.Enqueue("capacity", Topology.ShortKey, command.BookingId,
                new CapacityShort(command.BookingId, command.Reference, command.Teu, remaining));
            outcome = ReserveOutcome.Short;
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return outcome;
    }

    /// <summary>Gives a held unit back exactly once. A second release for the same booking changes nothing.</summary>
    public async Task<bool> ReleaseAsync(Guid messageId, ReleaseCapacity command, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await db.ClaimAsync(Name, messageId, ct))
        {
            await tx.CommitAsync(ct);
            return false;
        }

        var hold = await db.Holds.AsNoTracking().FirstOrDefaultAsync(h => h.BookingId == command.BookingId, ct);
        var released = false;
        if (hold is not null)
        {
            var now = DateTimeOffset.UtcNow;
            var flipped = await db.Holds
                .Where(h => h.BookingId == command.BookingId && h.Status == HoldStatus.Held)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(h => h.Status, HoldStatus.Released)
                    .SetProperty(h => h.ReleasedAt, now)
                    .SetProperty(h => h.ReleaseReason, command.Reason), ct);
            if (flipped == 1)
            {
                await db.Sailings
                    .Where(s => s.Id == hold.SailingId && s.RemainingTeu + hold.Teu <= s.CapacityTeu)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.RemainingTeu, x => x.RemainingTeu + hold.Teu), ct);
                db.Enqueue("capacity", Topology.ReleasedKey, command.BookingId,
                    new CapacityReleased(command.BookingId, hold.Teu, command.Reason));
                released = true;
            }
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return released;
    }
}
