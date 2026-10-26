using Freight.Bookings;
using Freight.Capacity;
using Freight.Documents;
using Freight.Shared.Auth;
using Freight.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Freight.IntegrationTests;

public static class TestDatabase
{
    public static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("FREIGHT_TEST_DB")
        ?? "Host=localhost;Port=5470;Database=freight_test;Username=freight;Password=freight";

    private static DbContextOptions<T> Options<T>(string schema) where T : DbContext =>
        new DbContextOptionsBuilder<T>()
            .UseNpgsql(ConnectionString, n => n.MigrationsHistoryTable("__EFMigrationsHistory", schema))
            .Options;

    public static BookingsDbContext Bookings() => new(Options<BookingsDbContext>(BookingsDbContext.SchemaName));
    public static CapacityDbContext Capacity() => new(Options<CapacityDbContext>(CapacityDbContext.SchemaName));
    public static DocumentsDbContext Documents() => new(Options<DocumentsDbContext>(DocumentsDbContext.SchemaName));

    /// <summary>Migrates the business modules and empties their tables. The identity schema is left alone: the API tests share it.</summary>
    public static async Task OpenAsync()
    {
        await using var bookings = Bookings();
        await using var capacity = Capacity();
        await using var documents = Documents();
        await bookings.Database.MigrateAsync();
        await capacity.Database.MigrateAsync();
        await documents.Database.MigrateAsync();
        await bookings.Database.ExecuteSqlRawAsync(
            "TRUNCATE bookings.\"InboxMessages\", bookings.\"OutboxMessages\", bookings.\"Bookings\", " +
            "capacity.\"InboxMessages\", capacity.\"OutboxMessages\", capacity.\"CapacityHolds\", capacity.\"Sailings\", " +
            "documents.\"InboxMessages\", documents.\"OutboxMessages\", documents.\"BillsOfLading\" RESTART IDENTITY CASCADE;");
    }

    public static async Task<Sailing> AddSailingAsync(int capacityTeu)
    {
        await using var db = Capacity();
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

    public static async Task<Booking> AddBookingAsync(Sailing sailing, string reference, int teu, BookingStatus status, Guid? ownerId = null)
    {
        await using var db = Bookings();
        var booking = new Booking
        {
            Id = Guid.CreateVersion7(),
            OwnerId = ownerId ?? Guid.CreateVersion7(),
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

public sealed class TestUser(Guid? id, bool staff = false) : ICurrentUser
{
    public bool IsAuthenticated => id is not null;
    public Guid? Id => id;
    public bool IsStaff => staff;
}

public sealed class FixedCatalog(params Sailing[] sailings) : ISailingCatalog
{
    public Task<SailingInfo?> FindAsync(string code, CancellationToken ct) =>
        Task.FromResult(sailings.Where(s => s.Code == code).Select(s => new SailingInfo(s.Id, s.Code)).FirstOrDefault());
}
