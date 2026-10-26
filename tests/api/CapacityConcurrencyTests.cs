using System.Collections.Concurrent;
using Freight.Api.Messaging;
using Freight.Api.Modules.Capacity;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Freight.Api.Tests;

public class CapacityConcurrencyTests
{
    [Fact]
    public async Task LastUnit_TwoConcurrentReservations_OneHeldOneShortNoHoldForTheLoser()
    {
        var db = await TestDatabase.OpenAsync();
        var sailing = await TestDatabase.AddSailingAsync(db, capacityTeu: 1);

        var outcomes = new ConcurrentBag<(Guid Booking, ReserveOutcome Outcome)>();
        var gate = new TaskCompletionSource();
        var tasks = Enumerable.Range(0, 2).Select(n => Task.Run(async () =>
        {
            await gate.Task;
            var bookingId = Guid.CreateVersion7();
            var handler = new CapacityHandler(TestDatabase.Create());
            var outcome = await handler.ReserveAsync(
                Guid.CreateVersion7(), new ReserveCapacity(bookingId, sailing.Id, 1, $"BK-{n}"));
            outcomes.Add((bookingId, outcome));
        })).ToArray();
        gate.SetResult();
        await Task.WhenAll(tasks);

        var check = TestDatabase.Create();
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
        var db = await TestDatabase.OpenAsync();
        var sailing = await TestDatabase.AddSailingAsync(db, capacityTeu: 4);
        var bookingId = Guid.CreateVersion7();
        var handler = new CapacityHandler(TestDatabase.Create());
        await handler.ReserveAsync(Guid.CreateVersion7(), new ReserveCapacity(bookingId, sailing.Id, 3, "BK-1"));

        // Two different commands (poison compensation and a reversal) both ask for the same unit back.
        var first = await new CapacityHandler(TestDatabase.Create())
            .ReleaseAsync(Guid.CreateVersion7(), new ReleaseCapacity(bookingId, "poison"));
        var second = await new CapacityHandler(TestDatabase.Create())
            .ReleaseAsync(Guid.CreateVersion7(), new ReleaseCapacity(bookingId, "reversal"));

        var remaining = await TestDatabase.Create().Sailings.Where(s => s.Id == sailing.Id).Select(s => s.RemainingTeu).FirstAsync();
        Assert.True(first);
        Assert.False(second);
        Assert.Equal(4, remaining);
    }

    [Fact]
    public async Task SameMessageDeliveredTwice_IsClaimedOnce()
    {
        var db = await TestDatabase.OpenAsync();
        var sailing = await TestDatabase.AddSailingAsync(db, capacityTeu: 5);
        var messageId = Guid.CreateVersion7();
        var command = new ReserveCapacity(Guid.CreateVersion7(), sailing.Id, 2, "BK-1");

        var first = await new CapacityHandler(TestDatabase.Create()).ReserveAsync(messageId, command);
        var second = await new CapacityHandler(TestDatabase.Create()).ReserveAsync(messageId, command);

        var remaining = await TestDatabase.Create().Sailings.Where(s => s.Id == sailing.Id).Select(s => s.RemainingTeu).FirstAsync();
        Assert.Equal(ReserveOutcome.Held, first);
        Assert.Equal(ReserveOutcome.Duplicate, second);
        Assert.Equal(3, remaining);
    }
}
