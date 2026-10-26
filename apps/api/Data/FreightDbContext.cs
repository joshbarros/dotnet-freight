using Freight.Api.Messaging;
using Freight.Api.Modules.Bookings;
using Freight.Api.Modules.Capacity;
using Freight.Api.Modules.Documents;
using Microsoft.EntityFrameworkCore;

namespace Freight.Api.Data;

public class FreightDbContext(DbContextOptions<FreightDbContext> options) : DbContext(options)
{
    public DbSet<Sailing> Sailings => Set<Sailing>();
    public DbSet<CapacityHold> Holds => Set<CapacityHold>();
    public DbSet<Booking> Bookings => Set<Booking>();
    public DbSet<BillOfLading> Documents => Set<BillOfLading>();
    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();
    public DbSet<InboxMessage> Inbox => Set<InboxMessage>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Sailing>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Code).IsUnique();
            e.Property(x => x.Code).HasMaxLength(20);
            e.ToTable(t => t.HasCheckConstraint("CK_Sailings_Remaining", "\"RemainingTeu\" >= 0 AND \"RemainingTeu\" <= \"CapacityTeu\""));
        });

        b.Entity<CapacityHold>(e =>
        {
            e.ToTable("CapacityHolds");
            e.HasKey(x => x.BookingId);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.HasOne<Sailing>().WithMany().HasForeignKey(x => x.SailingId);
        });

        b.Entity<Booking>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Reference).HasMaxLength(BookingRules.MaxReferenceLength);
            e.HasIndex(x => new { x.SailingId, x.Reference }).IsUnique();
            e.HasOne<Sailing>().WithMany().HasForeignKey(x => x.SailingId);
        });

        b.Entity<BillOfLading>(e =>
        {
            e.ToTable("BillsOfLading");
            e.HasKey(x => x.BookingId);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
        });

        b.Entity<OutboxMessage>(e =>
        {
            e.ToTable("OutboxMessages");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).UseIdentityAlwaysColumn();
            e.Property(x => x.Payload).HasColumnType("jsonb");
            e.HasIndex(x => x.SentAt).HasFilter("\"SentAt\" IS NULL");
        });

        b.Entity<InboxMessage>(e =>
        {
            e.ToTable("InboxMessages");
            e.HasKey(x => new { x.Consumer, x.MessageId });
        });
    }
}
