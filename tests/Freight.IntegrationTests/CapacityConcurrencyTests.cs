using System.Collections.Concurrent;
using Freight.Capacity;
using Freight.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Freight.IntegrationTests;

public class CapacityConcurrencyTests
{
    [Fact]
    public async Task LastUnit_TwoConcurrentReservations_OneHeldOneShortNoHoldForTheLoser()
    {
        await TestDatabase.OpenAsync();
        var sailing = await TestDatabase.AddSailingAsync(capacityTeu: 1);

        var outcomes = new ConcurrentBag<(Guid Booking, ReserveOutcome Outcome)>();
        var gate = new TaskCompletionSource();
        var tasks = Enumerable.Range(0, 2).Select(n => Task.Run(async () =>
        {
            await gate.Task;
            var bookingId = Guid.CreateVersion7();
            await using var db = TestDatabase.Capacity();
            var outcome = await new CapacityHandler(db).ReserveAsync(
                Guid.CreateVersion7(), new ReserveCapacity(bookingId, Guid.NewGuid(), sailing.Id, 1, $"BK-{n}"));
            outcomes.Add((bookingId, outcome));
        })).ToArray();
        gate.SetResult();
        await Task.WhenAll(tasks);

        await using var check = TestDatabase.Capacity();
        var remaining = await check.Sailings.Where(s => s.Id == sailing.Id).Select(s => s.RemainingTeu).FirstAsync();
        var holds = await check.Holds.ToListAsync();
        var loser = outcomes.Single(o => o.Outcome == ReserveOutcome.Short).Booking;

        Assert.Single(outcomes, o => o.Outcome == ReserveOutcome.Held);
        Assert.Single(outcomes, o => o.Outcome == ReserveOutcome.Short);
        Assert.Equal(0, remaining);
        Assert.Single(holds);
        Assert.DoesNotContain(holds, h => h.BookingId == loser);
        Assert.Equal(1, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.HeldKey));
        Assert.Equal(1, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.ShortKey));
    }

    [Fact]
    public async Task Release_GivesTheUnitBackExactlyOnce()
    {
        await TestDatabase.OpenAsync();
        var sailing = await TestDatabase.AddSailingAsync(capacityTeu: 4);
        var bookingId = Guid.CreateVersion7();
        await using (var db = TestDatabase.Capacity())
            await new CapacityHandler(db).ReserveAsync(Guid.CreateVersion7(), new ReserveCapacity(bookingId, Guid.NewGuid(), sailing.Id, 3, "BK-1"));

        // Two different commands (poison compensation and a reversal) both ask for the same unit back.
        await using var first = TestDatabase.Capacity();
        await using var second = TestDatabase.Capacity();
        var one = await new CapacityHandler(first).ReleaseAsync(Guid.CreateVersion7(), new ReleaseCapacity(bookingId, "poison"));
        var two = await new CapacityHandler(second).ReleaseAsync(Guid.CreateVersion7(), new ReleaseCapacity(bookingId, "reversal"));

        await using var check = TestDatabase.Capacity();
        Assert.True(one);
        Assert.False(two);
        Assert.Equal(4, await check.Sailings.Where(s => s.Id == sailing.Id).Select(s => s.RemainingTeu).FirstAsync());
    }

    [Fact]
    public async Task SameMessageDeliveredTwice_IsClaimedOnce()
    {
        await TestDatabase.OpenAsync();
        var sailing = await TestDatabase.AddSailingAsync(capacityTeu: 5);
        var messageId = Guid.CreateVersion7();
        var command = new ReserveCapacity(Guid.CreateVersion7(), Guid.NewGuid(), sailing.Id, 2, "BK-1");

        await using var a = TestDatabase.Capacity();
        await using var b = TestDatabase.Capacity();
        var first = await new CapacityHandler(a).ReserveAsync(messageId, command);
        var second = await new CapacityHandler(b).ReserveAsync(messageId, command);

        await using var check = TestDatabase.Capacity();
        Assert.Equal(ReserveOutcome.Held, first);
        Assert.Equal(ReserveOutcome.Duplicate, second);
        Assert.Equal(3, await check.Sailings.Where(s => s.Id == sailing.Id).Select(s => s.RemainingTeu).FirstAsync());
    }

    [Fact]
    public async Task TheDatabaseItself_RefusesANegativeRemainder()
    {
        await TestDatabase.OpenAsync();
        var sailing = await TestDatabase.AddSailingAsync(capacityTeu: 2);
        await using var db = TestDatabase.Capacity();

        // A bug that skipped the WHERE clause would hit the check constraint instead of overselling.
        await Assert.ThrowsAnyAsync<Exception>(() => db.Sailings.Where(s => s.Id == sailing.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RemainingTeu, x => x.RemainingTeu - 3)));
    }
}
