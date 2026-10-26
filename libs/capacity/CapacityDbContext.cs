using Freight.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Freight.Capacity;

public class CapacityDbContext(DbContextOptions<CapacityDbContext> options) : ModuleDbContext(options, SchemaName)
{
    public const string SchemaName = "capacity";

    public DbSet<Sailing> Sailings => Set<Sailing>();
    public DbSet<CapacityHold> Holds => Set<CapacityHold>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

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
    }
}
