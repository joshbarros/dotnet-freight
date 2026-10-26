namespace Freight.Api.Modules.Capacity;

public class Sailing
{
    public Guid Id { get; set; }
    public string Code { get; set; } = "";
    public string Vessel { get; set; } = "";
    public string Origin { get; set; } = "";
    public string Destination { get; set; } = "";
    public DateTimeOffset Departure { get; set; }
    public int CapacityTeu { get; set; }
    public int RemainingTeu { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public enum HoldStatus { Held, Released }

public class CapacityHold
{
    public Guid BookingId { get; set; }
    public Guid SailingId { get; set; }
    public int Teu { get; set; }
    public HoldStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ReleasedAt { get; set; }
    public string? ReleaseReason { get; set; }
}
