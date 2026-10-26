using Freight.Shared;
using Freight.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Freight.Documents;

public enum DocumentStatus { Issued, Rejected }

public class BillOfLading
{
    public Guid BookingId { get; set; }
    public Guid OwnerId { get; set; }
    public string? Number { get; set; }
    public DocumentStatus Status { get; set; }
    public string? Reason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public class DocumentsDbContext(DbContextOptions<DocumentsDbContext> options) : ModuleDbContext(options, SchemaName)
{
    public const string SchemaName = "documents";

    public DbSet<BillOfLading> Documents => Set<BillOfLading>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.Entity<BillOfLading>(e =>
        {
            e.ToTable("BillsOfLading");
            e.HasKey(x => x.BookingId);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
        });
    }
}

public enum IssueOutcome { Issued, Rejected, Duplicate }

/// <summary>Issues the bill of lading once capacity is held. A poison reference is committed as rejected.</summary>
public class DocumentsHandler(DocumentsDbContext db) : IMessageHandler
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
        if (PoisonRule.IsPoison(held.Reference))
        {
            const string reason = "Reference contains the poison token.";
            db.Documents.Add(new BillOfLading
            {
                BookingId = held.BookingId,
                OwnerId = held.OwnerId,
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
                OwnerId = held.OwnerId,
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
