using Freight.Shared;
using Freight.Shared.Auth;
using Freight.Shared.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Freight.Capacity;

public record CreateSailingRequest(string? Code, string? Vessel, string? Origin, string? Destination, DateTimeOffset? Departure, long CapacityTeu);

public record SailingDto(Guid Id, string Code, string Vessel, string Origin, string Destination, DateTimeOffset Departure, int CapacityTeu, int RemainingTeu)
{
    public static SailingDto From(Sailing s) => new(s.Id, s.Code, s.Vessel, s.Origin, s.Destination, s.Departure, s.CapacityTeu, s.RemainingTeu);
}

/// <summary>What other modules may ask Capacity, over Capacity's own tables.</summary>
public sealed class SailingCatalog(CapacityDbContext db) : ISailingCatalog
{
    public async Task<SailingInfo?> FindAsync(string code, CancellationToken ct) =>
        await db.Sailings.AsNoTracking().Where(s => s.Code == code).Select(s => new SailingInfo(s.Id, s.Code)).FirstOrDefaultAsync(ct);
}

[ApiController]
[Route("api/sailings")]
[Authorize]
public class SailingsController(CapacityDbContext db) : ControllerBase
{
    private const int MaxCapacityTeu = 5000;

    [HttpGet]
    public async Task<IEnumerable<SailingDto>> List(CancellationToken ct) =>
        (await db.Sailings.AsNoTracking().OrderBy(s => s.Departure).ThenBy(s => s.Code).ToListAsync(ct)).Select(SailingDto.From);

    [HttpGet("{code}")]
    public async Task<ActionResult<SailingDto>> Get(string code, CancellationToken ct)
    {
        var sailing = await db.Sailings.AsNoTracking().FirstOrDefaultAsync(s => s.Code == code, ct);
        return sailing is null ? NotFound() : SailingDto.From(sailing);
    }

    /// <summary>Opens a sailing. Staff only. This is the only place RemainingTeu is initialised; after that only the Capacity handler writes it.</summary>
    [HttpPost]
    [Authorize(Policy = Policies.ManageCatalog)]
    public async Task<ActionResult<SailingDto>> Create(CreateSailingRequest request, CancellationToken ct)
    {
        var code = request.Code?.Trim();
        if (string.IsNullOrEmpty(code) || code.Length > 20)
            throw new DomainException(400, "Code is required and at most 20 characters.");
        if (string.IsNullOrWhiteSpace(request.Vessel) || string.IsNullOrWhiteSpace(request.Origin) || string.IsNullOrWhiteSpace(request.Destination))
            throw new DomainException(400, "Vessel, origin and destination are required.");
        if (request.CapacityTeu < 1 || request.CapacityTeu > MaxCapacityTeu)
            throw new DomainException(400, $"Capacity must be between 1 and {MaxCapacityTeu} TEU.");
        if (await db.Sailings.AnyAsync(s => s.Code == code, ct))
            throw new DomainException(409, $"Sailing '{code}' already exists.");

        var sailing = new Sailing
        {
            Id = Guid.CreateVersion7(),
            Code = code,
            Vessel = request.Vessel.Trim(),
            Origin = request.Origin.Trim(),
            Destination = request.Destination.Trim(),
            Departure = request.Departure ?? DateTimeOffset.UtcNow.AddDays(14),
            CapacityTeu = (int)request.CapacityTeu,
            RemainingTeu = (int)request.CapacityTeu,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Sailings.Add(sailing);
        await db.SaveChangesAsync(ct);
        return Created($"/api/sailings/{sailing.Code}", SailingDto.From(sailing));
    }
}
