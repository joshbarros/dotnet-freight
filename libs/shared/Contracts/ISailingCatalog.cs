namespace Freight.Shared.Contracts;

public record SailingInfo(Guid Id, string Code);

/// <summary>
/// What Bookings may ask Capacity, without referencing it. Capacity implements it over its own tables;
/// Bookings never reads another module's schema.
/// </summary>
public interface ISailingCatalog
{
    Task<SailingInfo?> FindAsync(string code, CancellationToken ct);
}
