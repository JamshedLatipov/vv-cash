using System;

namespace VvCash.Services;

/// <summary>Converts between a product's base unit — always a piece — and its
/// secondary unit (m², running metre, kilogram…).
///
/// A deliberate mirror of the server's <c>units.ConvertToBase</c>. The server
/// re-derives every line it receives and refuses the document when
/// <c>|quantity_in_unit − quantity × factor|</c> leaves its tolerance, so a
/// register that rounds differently gets its own already-printed receipts
/// rejected. Any change here has to be made on both sides at once.</summary>
public static class UnitConverter
{
    /// <summary>Where the piece count is cut for divisible goods. 12.5 / 0.24
    /// does not terminate, so the cut is explicit and identical on every
    /// client. Matches the server's <c>divisibleScale</c>.</summary>
    public const int DivisibleScale = 6;

    /// <summary>Turns an amount in the secondary unit into pieces.
    ///
    /// <paramref name="factor"/> is how many secondary units fit into one
    /// piece: 0.24 m² per tile.
    ///
    /// For an indivisible product the piece count rounds up and the returned
    /// unit amount is recomputed from it — the customer pays for whole pieces.
    /// Returning the requested amount instead would break the server's
    /// quantity × factor ≈ quantity_in_unit invariant.</summary>
    public static (decimal Quantity, decimal QuantityInUnit) ToBase(
        decimal amount, decimal factor, bool isDivisible)
    {
        if (factor <= 0m)
            throw new ArgumentOutOfRangeException(
                nameof(factor), factor, "unit factor must be greater than zero");
        if (amount <= 0m)
            throw new ArgumentOutOfRangeException(
                nameof(amount), amount, "amount must be greater than zero");

        if (isDivisible)
        {
            // AwayFromZero, not .NET's default ToEven: the server's DivRound
            // rounds half away from zero, and banker's rounding would put the
            // two sides on different piece counts for an exact midpoint.
            var pieces = Math.Round(amount / factor, DivisibleScale, MidpointRounding.AwayFromZero);
            return (pieces, amount);
        }

        // Exact remainder rather than Math.Ceiling over the quotient. decimal
        // division rounds at the 28th significant digit, so a small factor with
        // a large amount can produce a quotient that has already rounded up,
        // and Ceiling would then add a piece nobody asked for. decimal
        // multiplication is exact, so comparing the product back against the
        // amount is safe at any factor. The server takes the same precaution
        // with QuoRem instead of Div.
        var whole = decimal.Truncate(amount / factor);
        if (whole * factor < amount) whole += 1m;
        return (whole, whole * factor);
    }

    /// <summary>The reverse view: how many secondary units a piece count amounts
    /// to. Used for display, and to recompute a line whose piece count was
    /// changed by the +/− stepper.</summary>
    public static decimal ToUnit(decimal quantity, decimal factor) => quantity * factor;
}
