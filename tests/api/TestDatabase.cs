using Freight.Api.Data;
using Freight.Api.Modules.Bookings;
using Freight.Api.Modules.Capacity;
using Microsoft.EntityFrameworkCore;

namespace Freight.Api.Tests;

public static class TestDatabase
{
    public static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("FREIGHT_TEST_DB")
        ?? "Host=localhost;Port=5470;Database=freight_test;Username=freight;Password=freight";

    private static readonly DbContextOptions<FreightDbContext> Options =
        new DbContextOptionsBuilder<FreightDbContext>().UseNpgsql(ConnectionString).Options;

    public static FreightDbContext Create() => new(Options);

    public static async Task<FreightDbContext> OpenAsync()
    {
        var db = Create();
        await db.Database.MigrateAsync();
        await db.Database.ExecuteSqlRawAsync(
            "TRUNCATE \"InboxMessages\", \"OutboxMessages\", \"BillsOfLading\", \"CapacityHolds\", \"Bookings\", \"Sailings\" RESTART IDENTITY CASCADE;");
        return db;
    }

    public static async Task<Sailing> AddSailingAsync(FreightDbContext db, int capacityTeu)
    {
        var sailing = new Sailing
        {
            Id = Guid.CreateVersion7(),
            Code = $"T-{Guid.NewGuid():N}"[..14],
            Vessel = "MV Test",
            Origin = "Santos",
            Destination = "Rotterdam",
            Departure = DateTimeOffset.UtcNow.AddDays(7),
            CapacityTeu = capacityTeu,
            RemainingTeu = capacityTeu,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Sailings.Add(sailing);
        await db.SaveChangesAsync();
        return sailing;
    }

    public static async Task<Booking> AddBookingAsync(FreightDbContext db, Sailing sailing, string reference, int teu, BookingStatus status)
    {
        var booking = new Booking
        {
            Id = Guid.CreateVersion7(),
            SailingId = sailing.Id,
            SailingCode = sailing.Code,
            Reference = reference,
            Teu = teu,
            FeeCents = BookingRules.FeeCents(teu),
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Bookings.Add(booking);
        await db.SaveChangesAsync();
        return booking;
    }
}
