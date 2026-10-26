using Freight.Bookings;
using Freight.Documents;
using Freight.Shared;
using Freight.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace Freight.IntegrationTests;

public class SagaTests
{
    private static BookingsHandler Handler(BookingsDbContext db) => new(db, Options.Create(new BookingOptions()));

    [Fact]
    public async Task PoisonReference_IsCommittedRejected_ThenCompensatingCommandIsQueued()
    {
        await TestDatabase.OpenAsync();
        var sailing = await TestDatabase.AddSailingAsync(5);
        var booking = await TestDatabase.AddBookingAsync(sailing, "BK-POISON-1", 2, BookingStatus.Held);
        var held = new CapacityHeld(booking.Id, booking.OwnerId, sailing.Id, 2, booking.Reference);

        await using var docs = TestDatabase.Documents();
        var ex = await Assert.ThrowsAsync<PoisonMessageException>(() =>
            new DocumentsHandler(docs).HandleAsync(new Delivery(Guid.CreateVersion7(), Topology.HeldKey, Json.Serialize(held)), default));
        Assert.Contains("poison", ex.Message, StringComparison.OrdinalIgnoreCase);

        await using var check = TestDatabase.Documents();
        var doc = await check.BillsOfLading.SingleAsync(d => d.BookingId == booking.Id);
        Assert.Equal(DocumentStatus.Rejected, doc.Status); // committed before the dead-letter
        Assert.Equal(booking.OwnerId, doc.OwnerId);
        Assert.Equal(1, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.ReleaseKey));
        Assert.Equal(1, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.RejectedKey));
        Assert.Equal(0, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.IssuedKey));
    }

    [Fact]
    public async Task Reversal_ThenFinalMark_LeavesBookingReversedAndDocumentIssued()
    {
        await TestDatabase.OpenAsync();
        var sailing = await TestDatabase.AddSailingAsync(5);
        var owner = Guid.CreateVersion7();
        var booking = await TestDatabase.AddBookingAsync(sailing, "BK-1", 2, BookingStatus.Held, owner);

        // The bill of lading is issued; the final mark has not landed yet.
        await using (var docs = TestDatabase.Documents())
            await new DocumentsHandler(docs).IssueAsync(Guid.CreateVersion7(), new CapacityHeld(booking.Id, owner, sailing.Id, 2, booking.Reference));

        // The owner reverses first.
        await using (var bookings = TestDatabase.Bookings())
            await new BookingService(bookings, new FixedCatalog(sailing), new TestUser(owner)).ReverseAsync(booking.Id, default);

        await using var late = TestDatabase.Bookings();
        var mark = await Handler(late).MarkIssuedAsync(Guid.CreateVersion7(), new BillOfLadingIssued(booking.Id, "BL-TEST"));

        await using var check = TestDatabase.Bookings();
        await using var checkDocs = TestDatabase.Documents();
        Assert.Equal(MarkOutcome.Skipped, mark);
        Assert.Equal(BookingStatus.Reversed, (await check.Bookings.SingleAsync()).Status);
        Assert.Equal(DocumentStatus.Issued, (await checkDocs.BillsOfLading.SingleAsync()).Status);
    }

    [Fact]
    public async Task FinalMark_RunsOnlyFromHeld()
    {
        await TestDatabase.OpenAsync();
        var sailing = await TestDatabase.AddSailingAsync(5);
        var held = await TestDatabase.AddBookingAsync(sailing, "BK-A", 1, BookingStatus.Held);
        var rejected = await TestDatabase.AddBookingAsync(sailing, "BK-B", 1, BookingStatus.Rejected);
        var requested = await TestDatabase.AddBookingAsync(sailing, "BK-C", 1, BookingStatus.Requested);

        await using (var a = TestDatabase.Bookings())
            Assert.Equal(MarkOutcome.Marked, await Handler(a).MarkIssuedAsync(Guid.CreateVersion7(), new BillOfLadingIssued(held.Id, "BL-1")));
        await using (var b = TestDatabase.Bookings())
            Assert.Equal(MarkOutcome.Skipped, await Handler(b).MarkIssuedAsync(Guid.CreateVersion7(), new BillOfLadingIssued(rejected.Id, "BL-2")));
        // Not Held yet: the message is retried later instead of being dropped.
        await using (var c = TestDatabase.Bookings())
            await Assert.ThrowsAsync<OutOfOrderException>(() => Handler(c).MarkIssuedAsync(Guid.CreateVersion7(), new BillOfLadingIssued(requested.Id, "BL-3")));

        await using var check = TestDatabase.Bookings();
        var statuses = (await check.Bookings.ToListAsync()).ToDictionary(b => b.Reference, b => b.Status);
        Assert.Equal(BookingStatus.Issued, statuses["BK-A"]);
        Assert.Equal(BookingStatus.Rejected, statuses["BK-B"]);
        Assert.Equal(BookingStatus.Requested, statuses["BK-C"]);
    }

    [Fact]
    public async Task Reversal_OfARejectedBooking_Conflicts()
    {
        await TestDatabase.OpenAsync();
        var sailing = await TestDatabase.AddSailingAsync(5);
        var owner = Guid.CreateVersion7();
        var booking = await TestDatabase.AddBookingAsync(sailing, "BK-1", 1, BookingStatus.Rejected, owner);

        await using var db = TestDatabase.Bookings();
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            new BookingService(db, new FixedCatalog(sailing), new TestUser(owner)).ReverseAsync(booking.Id, default));
        Assert.Equal(409, ex.Status);
    }

    [Fact]
    public async Task Reversal_ByAnotherCustomer_IsNotFound_AndChangesNothing()
    {
        await TestDatabase.OpenAsync();
        var sailing = await TestDatabase.AddSailingAsync(5);
        var booking = await TestDatabase.AddBookingAsync(sailing, "BK-1", 1, BookingStatus.Held);

        await using var db = TestDatabase.Bookings();
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            new BookingService(db, new FixedCatalog(sailing), new TestUser(Guid.NewGuid())).ReverseAsync(booking.Id, default));

        Assert.Equal(404, ex.Status);
        await using var check = TestDatabase.Bookings();
        Assert.Equal(BookingStatus.Held, (await check.Bookings.SingleAsync()).Status);
        Assert.Equal(0, await check.Outbox.CountAsync());
    }

    [Fact]
    public async Task Staff_CanReverseAnyonesBooking()
    {
        await TestDatabase.OpenAsync();
        var sailing = await TestDatabase.AddSailingAsync(5);
        var booking = await TestDatabase.AddBookingAsync(sailing, "BK-1", 1, BookingStatus.Held);

        await using var db = TestDatabase.Bookings();
        var reversed = await new BookingService(db, new FixedCatalog(sailing), new TestUser(Guid.NewGuid(), staff: true)).ReverseAsync(booking.Id, default);
        Assert.Equal(BookingStatus.Reversed, reversed.Status);
    }

    [Fact]
    public async Task Create_RecordsTheCallerAsTheOwner_AndQueuesTheReservation()
    {
        await TestDatabase.OpenAsync();
        var sailing = await TestDatabase.AddSailingAsync(5);
        var owner = Guid.CreateVersion7();

        await using var db = TestDatabase.Bookings();
        var booking = await new BookingService(db, new FixedCatalog(sailing), new TestUser(owner))
            .CreateAsync(new CreateBookingRequest(sailing.Code, "BK-1", 2), default);

        await using var check = TestDatabase.Bookings();
        Assert.Equal(owner, (await check.Bookings.SingleAsync()).OwnerId);
        var message = await check.Outbox.SingleAsync();
        Assert.Equal(Topology.ReserveKey, message.RoutingKey);
        Assert.Equal(owner, Json.Parse<ReserveCapacity>(message.Payload).OwnerId);
        Assert.Equal(BookingStatus.Requested, booking.Status);
    }

    [Fact]
    public async Task Create_WithoutASignedInUser_IsRefused()
    {
        await TestDatabase.OpenAsync();
        var sailing = await TestDatabase.AddSailingAsync(5);
        await using var db = TestDatabase.Bookings();
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            new BookingService(db, new FixedCatalog(sailing), new TestUser(null)).CreateAsync(new CreateBookingRequest(sailing.Code, "BK-1", 1), default));
        Assert.Equal(401, ex.Status);
    }

    [Fact]
    public async Task Create_ForAnUnknownSailing_IsNotFound()
    {
        await TestDatabase.OpenAsync();
        await using var db = TestDatabase.Bookings();
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            new BookingService(db, new FixedCatalog(), new TestUser(Guid.NewGuid())).CreateAsync(new CreateBookingRequest("NOPE", "BK-1", 1), default));
        Assert.Equal(404, ex.Status);
    }
}
