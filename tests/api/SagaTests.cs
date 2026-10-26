using Freight.Api.Messaging;
using Freight.Api.Modules.Bookings;
using Freight.Api.Modules.Capacity;
using Freight.Api.Modules.Documents;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace Freight.Api.Tests;

public class SagaTests
{
    private static BookingsHandler Bookings() =>
        new(TestDatabase.Create(), Options.Create(new BookingOptions()));

    [Fact]
    public async Task PoisonReference_IsCommittedRejected_ThenCompensatingCommandIsQueued()
    {
        var db = await TestDatabase.OpenAsync();
        var sailing = await TestDatabase.AddSailingAsync(db, 5);
        var booking = await TestDatabase.AddBookingAsync(db, sailing, "BK-POISON-1", 2, BookingStatus.Held);
        var held = new CapacityHeld(booking.Id, sailing.Id, 2, booking.Reference);

        var handler = new DocumentsHandler(TestDatabase.Create());
        var ex = await Assert.ThrowsAsync<PoisonMessageException>(() =>
            handler.HandleAsync(new Delivery(Guid.CreateVersion7(), Topology.HeldKey, Json.Serialize(held)), default));
        Assert.Contains("poison", ex.Message, StringComparison.OrdinalIgnoreCase);

        var check = TestDatabase.Create();
        var doc = await check.Documents.SingleAsync(d => d.BookingId == booking.Id);
        Assert.Equal(DocumentStatus.Rejected, doc.Status); // committed before the dead-letter
        Assert.Equal(1, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.ReleaseKey));
        Assert.Equal(1, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.RejectedKey));
        Assert.Equal(0, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.IssuedKey));
    }

    [Fact]
    public async Task Reversal_ThenFinalMark_LeavesBookingReversedAndDocumentIssued()
    {
        var db = await TestDatabase.OpenAsync();
        var sailing = await TestDatabase.AddSailingAsync(db, 5);
        var booking = await TestDatabase.AddBookingAsync(db, sailing, "BK-1", 2, BookingStatus.Held);

        // The bill of lading is issued; the final mark has not landed yet.
        var number = "BL-TEST";
        await new DocumentsHandler(TestDatabase.Create()).IssueAsync(
            Guid.CreateVersion7(), new CapacityHeld(booking.Id, sailing.Id, 2, booking.Reference));

        // The reversal gets there first.
        await new BookingService(TestDatabase.Create()).ReverseAsync(booking.Id, default);

        var mark = await Bookings().MarkIssuedAsync(Guid.CreateVersion7(), new BillOfLadingIssued(booking.Id, number));

        var check = TestDatabase.Create();
        Assert.Equal(MarkOutcome.Skipped, mark);
        Assert.Equal(BookingStatus.Reversed, (await check.Bookings.SingleAsync()).Status);
        Assert.Equal(DocumentStatus.Issued, (await check.Documents.SingleAsync()).Status);
    }

    [Fact]
    public async Task FinalMark_RunsOnlyFromHeld()
    {
        var db = await TestDatabase.OpenAsync();
        var sailing = await TestDatabase.AddSailingAsync(db, 5);
        var held = await TestDatabase.AddBookingAsync(db, sailing, "BK-A", 1, BookingStatus.Held);
        var rejected = await TestDatabase.AddBookingAsync(db, sailing, "BK-B", 1, BookingStatus.Rejected);
        var requested = await TestDatabase.AddBookingAsync(db, sailing, "BK-C", 1, BookingStatus.Requested);

        Assert.Equal(MarkOutcome.Marked, await Bookings().MarkIssuedAsync(Guid.CreateVersion7(), new BillOfLadingIssued(held.Id, "BL-1")));
        Assert.Equal(MarkOutcome.Skipped, await Bookings().MarkIssuedAsync(Guid.CreateVersion7(), new BillOfLadingIssued(rejected.Id, "BL-2")));
        // Not Held yet: the message is retried later instead of being dropped.
        await Assert.ThrowsAsync<OutOfOrderException>(() =>
            Bookings().MarkIssuedAsync(Guid.CreateVersion7(), new BillOfLadingIssued(requested.Id, "BL-3")));

        var statuses = (await TestDatabase.Create().Bookings.ToListAsync()).ToDictionary(b => b.Reference, b => b.Status);
        Assert.Equal(BookingStatus.Issued, statuses["BK-A"]);
        Assert.Equal(BookingStatus.Rejected, statuses["BK-B"]);
        Assert.Equal(BookingStatus.Requested, statuses["BK-C"]);
    }

    [Fact]
    public async Task Reversal_OfARejectedBooking_Conflicts()
    {
        var db = await TestDatabase.OpenAsync();
        var sailing = await TestDatabase.AddSailingAsync(db, 5);
        var booking = await TestDatabase.AddBookingAsync(db, sailing, "BK-1", 1, BookingStatus.Rejected);

        var ex = await Assert.ThrowsAsync<DomainException>(() => new BookingService(TestDatabase.Create()).ReverseAsync(booking.Id, default));
        Assert.Equal(409, ex.Status);
    }
}
