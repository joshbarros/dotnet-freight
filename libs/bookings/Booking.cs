namespace Freight.Bookings;

public enum BookingStatus { Requested, Held, Issued, Reversed, Rejected }

public class Booking
{
    public Guid Id { get; set; }
    public Guid OwnerId { get; set; }
    public Guid SailingId { get; set; }
    public string SailingCode { get; set; } = "";
    public string Reference { get; set; } = "";
    public int Teu { get; set; }
    public long FeeCents { get; set; }
    public BookingStatus Status { get; set; }
    public string? Reason { get; set; }
    public string? BillOfLadingNumber { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? CapacityReleasedAt { get; set; }
}

public class BookingOptions
{
    /// <summary>Latency knob for the final mark. Compose sets it so the reversal race window is wide enough to hit on purpose.</summary>
    public int FinalMarkDelayMs { get; set; }
}

public record CreateBookingRequest(string? SailingCode, string? Reference, long Teu);

public record BookingDto(
    Guid Id, Guid OwnerId, string SailingCode, string Reference, int Teu, long FeeCents, string Status,
    string? Reason, string? BillOfLadingNumber, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    DateTimeOffset? CapacityReleasedAt)
{
    public static BookingDto From(Booking b) => new(
        b.Id, b.OwnerId, b.SailingCode, b.Reference, b.Teu, b.FeeCents, b.Status.ToString(),
        b.Reason, b.BillOfLadingNumber, b.CreatedAt, b.UpdatedAt, b.CapacityReleasedAt);
}
