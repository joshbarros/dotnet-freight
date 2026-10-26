using Freight.Api.Data;
using Freight.Api.Messaging;
using Freight.Api.Modules.Bookings;
using Microsoft.EntityFrameworkCore;

namespace Freight.Api.Modules.Documents;

public enum DocumentStatus { Issued, Rejected }

public class BillOfLading
{
    public Guid BookingId { get; set; }
    public string? Number { get; set; }
    public DocumentStatus Status { get; set; }
    public string? Reason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public enum IssueOutcome { Issued, Rejected, Duplicate }

/// <summary>Issues the bill of lading once capacity is held. A poison reference is committed as rejected.</summary>
public class DocumentsHandler(FreightDbContext db) : IMessageHandler
{
    public const string Name = "documents";

    public async Task HandleAsync(Delivery delivery, CancellationToken ct)
    {
        if (delivery.RoutingKey != Topology.HeldKey)
            throw new InvalidOperationException($"Documents does not handle '{delivery.RoutingKey}'.");

        var outcome = await IssueAsync(delivery.MessageId, Json.Parse<CapacityHeld>(delivery.Body), ct);
        if (outcome == IssueOutcome.Rejected)
            throw new PoisonMessageException("Reference carries the poison token; rejection is committed.");
    }

    public async Task<IssueOutcome> IssueAsync(Guid messageId, CapacityHeld held, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await db.ClaimAsync(Name, messageId, ct))
        {
            await tx.CommitAsync(ct);
            return IssueOutcome.Duplicate;
        }

        IssueOutcome outcome;
        if (BookingRules.IsPoison(held.Reference))
        {
            const string reason = "Reference contains the poison token.";
            db.Documents.Add(new BillOfLading
            {
                BookingId = held.BookingId,
                Status = DocumentStatus.Rejected,
                Reason = reason,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            db.Enqueue("documents", Topology.ReleaseKey, held.BookingId, new ReleaseCapacity(held.BookingId, "poison"));
            db.Enqueue("documents", Topology.RejectedKey, held.BookingId, new DocumentRejected(held.BookingId, reason));
            outcome = IssueOutcome.Rejected;
        }
        else
        {
            var number = $"BL-{held.BookingId.ToString("N")[^12..].ToUpperInvariant()}";
            db.Documents.Add(new BillOfLading
            {
                BookingId = held.BookingId,
                Number = number,
                Status = DocumentStatus.Issued,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            db.Enqueue("documents", Topology.IssuedKey, held.BookingId, new BillOfLadingIssued(held.BookingId, number));
            outcome = IssueOutcome.Issued;
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return outcome;
    }
}
