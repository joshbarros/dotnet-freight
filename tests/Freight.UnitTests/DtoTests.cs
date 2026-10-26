using Freight.Bookings;
using Freight.Capacity;
using Xunit;

namespace Freight.UnitTests;

public class DtoTests
{
    [Fact]
    public void BookingDto_MapsEveryFieldAndTheStatusName()
    {
        var now = DateTimeOffset.UtcNow;
        var booking = new Booking
        {
            Id = Guid.NewGuid(),
            OwnerId = Guid.NewGuid(),
            SailingId = Guid.NewGuid(),
            SailingCode = "S-1",
            Reference = "BK-1",
            Teu = 3,
            FeeCents = 277_500,
            Status = BookingStatus.Issued,
            BillOfLadingNumber = "BL-1",
            CreatedAt = now,
            UpdatedAt = now,
        };

        var dto = BookingDto.From(booking);

        Assert.Equal(booking.Id, dto.Id);
        Assert.Equal(booking.OwnerId, dto.OwnerId);
        Assert.Equal("Issued", dto.Status);
        Assert.Equal(277_500, dto.FeeCents);
        Assert.Equal("BL-1", dto.BillOfLadingNumber);
    }

    [Fact]
    public void SailingDto_MapsTheScarceNumber()
    {
        var sailing = new Sailing { Id = Guid.NewGuid(), Code = "S-1", Vessel = "v", Origin = "o", Destination = "d", CapacityTeu = 10, RemainingTeu = 4 };
        var dto = SailingDto.From(sailing);
        Assert.Equal(10, dto.CapacityTeu);
        Assert.Equal(4, dto.RemainingTeu);
    }
}
