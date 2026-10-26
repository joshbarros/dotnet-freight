using System.Text.Json;

namespace Freight.Api.Messaging;

public record ReserveCapacity(Guid BookingId, Guid SailingId, int Teu, string Reference);
public record CapacityHeld(Guid BookingId, Guid SailingId, int Teu, string Reference);
public record CapacityShort(Guid BookingId, string Reference, int RequestedTeu, int RemainingTeu);
public record ReleaseCapacity(Guid BookingId, string Reason);
public record CapacityReleased(Guid BookingId, int Teu, string Reason);
public record BillOfLadingIssued(Guid BookingId, string Number);
public record DocumentRejected(Guid BookingId, string Reason);

public record Delivery(Guid MessageId, string RoutingKey, string Body);

public interface IMessageHandler
{
    Task HandleAsync(Delivery delivery, CancellationToken ct);
}

/// <summary>Committed as rejected; the message must be dead-lettered, not retried.</summary>
public class PoisonMessageException(string message) : Exception(message);

/// <summary>The message arrived before the state it depends on; retry later.</summary>
public class OutOfOrderException(string message) : Exception(message);

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Parse<T>(string body) =>
        JsonSerializer.Deserialize<T>(body, Options) ?? throw new InvalidOperationException("Empty message body.");
}
