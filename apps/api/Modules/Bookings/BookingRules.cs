namespace Freight.Api.Modules.Bookings;

public static class BookingRules
{
    public const int MaxTeuPerBooking = 20;
    public const int MaxReferenceLength = 40;
    public const string PoisonToken = "POISON";
    public const long FreightCentsPerTeu = 90_000;
    public const long DocumentationFeeCents = 7_500;

    /// <summary>Returns an error message, or null when the request is acceptable.</summary>
    public static string? Validate(string? reference, long teu)
    {
        if (string.IsNullOrWhiteSpace(reference))
            return "Reference is required.";
        if (reference.Trim().Length > MaxReferenceLength)
            return $"Reference must be at most {MaxReferenceLength} characters.";
        if (teu < 1)
            return "TEU must be at least 1.";
        // Compared as long on purpose: narrowing 4294967297 to int would wrap to 1 and pass.
        if (teu > MaxTeuPerBooking)
            return $"A booking holds at most {MaxTeuPerBooking} TEU.";
        return null;
    }

    /// <summary>Case-sensitive substring match: a longer token that merely contains the poison matches too.</summary>
    public static bool IsPoison(string reference) => reference.Contains(PoisonToken, StringComparison.Ordinal);

    /// <summary>Freight per TEU plus a flat documentation fee, in cents. Overflow throws instead of wrapping.</summary>
    public static long FeeCents(int teu) => checked(teu * FreightCentsPerTeu + DocumentationFeeCents);
}
