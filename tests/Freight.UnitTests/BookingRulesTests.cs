using Freight.Bookings;
using Xunit;

namespace Freight.UnitTests;

public class BookingRulesTests
{
    [Theory]
    [InlineData(null, 1)]
    [InlineData("", 1)]
    [InlineData("   ", 1)]
    [InlineData("BK-1", 0)]
    [InlineData("BK-1", -3)]
    public void EmptyInput_IsRejected(string? reference, long teu) =>
        Assert.NotNull(BookingRules.Validate(reference, teu));

    [Fact]
    public void OverLimit_IsRejected()
    {
        Assert.Null(BookingRules.Validate("BK-1", BookingRules.MaxTeuPerBooking));
        Assert.NotNull(BookingRules.Validate("BK-1", BookingRules.MaxTeuPerBooking + 1));
        Assert.NotNull(BookingRules.Validate(new string('x', BookingRules.MaxReferenceLength + 1), 1));
    }

    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData((long)int.MaxValue + 1)]
    [InlineData(4_294_967_297L)] // (int) of this wraps to 1; it must not pass as one TEU
    [InlineData(long.MaxValue)]
    public void IntegerOverflow_IsRejectedNotWrapped(long teu)
    {
        Assert.NotNull(BookingRules.Validate("BK-1", teu));
        Assert.Equal(1, unchecked((int)4_294_967_297L)); // documents what the naive cast would have done
    }

    [Fact]
    public void HappyFee_IsFreightPerTeuPlusDocumentationFee()
    {
        Assert.Null(BookingRules.Validate("BK-1", 3));
        Assert.Equal(3 * 90_000L + 7_500L, BookingRules.FeeCents(3));
        Assert.Equal(90_000L + 7_500L, BookingRules.FeeCents(1));
    }
}
