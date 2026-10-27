using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Freight.Bookings;
using Freight.Documents;
using Xunit;

namespace Freight.IntegrationTests;

[Collection("api")]
public class AuthorizationApiTests(ApiFactory factory)
{
    [Theory]
    [InlineData("/api/sailings")]
    [InlineData("/api/bookings")]
    [InlineData("/api/documents/11111111-1111-1111-1111-111111111111")]
    [InlineData("/api/bookings/11111111-1111-1111-1111-111111111111/events")]
    public async Task EverythingButHealthAndAuth_RequiresASignedInUser(string path) =>
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.NewClient().GetAsync(path)).StatusCode);

    [Fact]
    public async Task WritesRequireASignedInUser()
    {
        var client = factory.NewClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/sailings", new { code = "X" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/bookings", new { sailingCode = "X" })).StatusCode);
    }

    [Fact]
    public async Task Health_IsOpen_AndReadinessChecksTheDatabases()
    {
        var client = factory.NewClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/health")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/health/ready")).StatusCode);
    }

    [Fact]
    public async Task ACustomer_CannotOpenASailing_ButAnAdministratorCan()
    {
        var customer = await factory.RegisterAsync();
        var admin = await factory.AdminAsync();
        var body = ApiHelpers.SailingBody(ApiHelpers.NewCode(), 10);

        Assert.Equal(HttpStatusCode.Forbidden, (await factory.ClientFor(customer.AccessToken).PostAsJsonAsync("/api/sailings", body)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await factory.ClientFor(admin.AccessToken).PostAsJsonAsync("/api/sailings", body)).StatusCode);
    }

    [Fact]
    public async Task ACustomer_CanReadTheCatalog()
    {
        var admin = await factory.AdminAsync();
        var code = await factory.CreateSailingAsync(admin);
        var customer = await factory.RegisterAsync();

        var sailing = await factory.ClientFor(customer.AccessToken).GetAsync($"/api/sailings/{code}");
        Assert.Equal(HttpStatusCode.OK, sailing.StatusCode);
    }

    [Fact]
    public async Task ABooking_BelongsToItsOwner()
    {
        var admin = await factory.AdminAsync();
        var sailing = await factory.CreateSailingAsync(admin);
        var ana = await factory.RegisterAsync();
        var bruno = await factory.RegisterAsync();
        var id = await factory.BookAsync(ana, sailing);

        Assert.Equal(HttpStatusCode.OK, (await factory.ClientFor(ana.AccessToken).GetAsync($"/api/bookings/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await factory.ClientFor(admin.AccessToken).GetAsync($"/api/bookings/{id}")).StatusCode);
        // Another customer learns nothing, not even that the id exists.
        Assert.Equal(HttpStatusCode.NotFound, (await factory.ClientFor(bruno.AccessToken).GetAsync($"/api/bookings/{id}")).StatusCode);
    }

    [Fact]
    public async Task AnotherCustomer_CannotReverseSomeoneElsesBooking()
    {
        var admin = await factory.AdminAsync();
        var sailing = await factory.CreateSailingAsync(admin);
        var ana = await factory.RegisterAsync();
        var bruno = await factory.RegisterAsync();
        var id = await factory.BookAsync(ana, sailing);

        var attempt = await factory.ClientFor(bruno.AccessToken).PostAsync($"/api/bookings/{id}/reversal", null);
        Assert.Equal(HttpStatusCode.NotFound, attempt.StatusCode);
    }

    [Fact]
    public async Task TheList_ShowsACustomerOnlyTheirOwnBookings_AndStaffEverything()
    {
        var admin = await factory.AdminAsync();
        var sailing = await factory.CreateSailingAsync(admin);
        var ana = await factory.RegisterAsync();
        var bruno = await factory.RegisterAsync();
        var anas = await factory.BookAsync(ana, sailing);
        var brunos = await factory.BookAsync(bruno, sailing);

        var seenByAna = await factory.ClientFor(ana.AccessToken).GetFromJsonAsync<JsonElement>($"/api/bookings?sailingCode={sailing}");
        var seenByStaff = await factory.ClientFor(admin.AccessToken).GetFromJsonAsync<JsonElement>($"/api/bookings?sailingCode={sailing}");

        Assert.Equal([anas], seenByAna.EnumerateArray().Select(b => b.GetProperty("id").GetGuid()).ToArray());
        Assert.Equivalent(new[] { anas, brunos }, seenByStaff.EnumerateArray().Select(b => b.GetProperty("id").GetGuid()).ToArray());
    }

    [Fact]
    public async Task TheBookingOwnerIsTheSignedInUser_NotWhatTheBodySays()
    {
        var admin = await factory.AdminAsync();
        var sailing = await factory.CreateSailingAsync(admin);
        var ana = await factory.RegisterAsync();

        var response = await factory.ClientFor(ana.AccessToken).PostAsJsonAsync("/api/bookings",
            ApiHelpers.BookingBody(sailing, "BK-OWNER-1", 1).Also("ownerId", Guid.NewGuid()));

        var booking = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(ana.UserId, booking.GetProperty("ownerId").GetGuid());
    }

    [Fact]
    public async Task TheEventLog_IsForStaffOnly()
    {
        var admin = await factory.AdminAsync();
        var sailing = await factory.CreateSailingAsync(admin);
        var ana = await factory.RegisterAsync();
        var id = await factory.BookAsync(ana, sailing);

        Assert.Equal(HttpStatusCode.Forbidden, (await factory.ClientFor(ana.AccessToken).GetAsync($"/api/bookings/{id}/events")).StatusCode);
    }

    [Fact]
    public async Task ADocument_IsVisibleToItsOwnerAndStaffOnly()
    {
        var admin = await factory.AdminAsync();
        var ana = await factory.RegisterAsync();
        var bruno = await factory.RegisterAsync();
        var bookingId = Guid.CreateVersion7();
        await using (var db = TestDatabase.Documents())
        {
            db.BillsOfLading.Add(new BillOfLading { BookingId = bookingId, OwnerId = ana.UserId, Number = "BL-X", Status = DocumentStatus.Issued, CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        Assert.Equal(HttpStatusCode.OK, (await factory.ClientFor(ana.AccessToken).GetAsync($"/api/documents/{bookingId}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await factory.ClientFor(admin.AccessToken).GetAsync($"/api/documents/{bookingId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await factory.ClientFor(bruno.AccessToken).GetAsync($"/api/documents/{bookingId}")).StatusCode);
    }

    [Fact]
    public async Task Validation_StillAppliesToSignedInUsers()
    {
        var admin = await factory.AdminAsync();
        var sailing = await factory.CreateSailingAsync(admin);
        var ana = await factory.RegisterAsync();
        var client = factory.ClientFor(ana.AccessToken);

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/bookings", ApiHelpers.BookingBody(sailing, "", 1))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/bookings", ApiHelpers.BookingBody(sailing, "BK", BookingRules.MaxTeuPerBooking + 1))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/api/bookings", ApiHelpers.BookingBody("NOPE", "BK", 1))).StatusCode);
    }

    [Fact]
    public async Task EveryResponse_CarriesSecurityHeaders_AndEchoesTheCorrelationId()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/sailings");
        request.Headers.Add("X-Correlation-Id", "trace-abc-123");

        var response = await factory.NewClient().SendAsync(request);

        Assert.Equal("trace-abc-123", response.Headers.GetValues("X-Correlation-Id").Single());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Contains("default-src 'none'", response.Headers.GetValues("Content-Security-Policy").Single());
    }

    [Fact]
    public async Task ProblemResponses_AreProblemJson_WithTheCorrelationId()
    {
        var admin = await factory.AdminAsync();
        var response = await factory.ClientFor(admin.AccessToken).PostAsJsonAsync("/api/sailings", new { code = "" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(problem.TryGetProperty("correlationId", out _));
    }
}
