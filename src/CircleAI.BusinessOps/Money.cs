// Money.cs — (0.1.0) Currency-aware money value type for BusinessOps.
//
// Money is a decimal Amount tagged with an ISO-4217 currency code. Arithmetic is
// guarded so two different currencies never silently combine — a mixed-currency
// add throws rather than producing a meaningless number. This is deliberately a
// tiny value type, not a full financial library: it covers the invoicing maths a
// small business needs (sum lines, apply tax, take payments), fully offline.
//
// decimal (not double) because money must not carry binary floating-point error.

using System;
using System.Collections.Generic;
using System.Globalization;

namespace CircleAI.BusinessOps;

/// <summary>An amount of money in a single currency.</summary>
public readonly record struct Money
{
    /// <summary>The numeric amount. May be negative (e.g. a credit or overpayment).</summary>
    public decimal Amount { get; init; }

    /// <summary>ISO-4217 currency code, upper-cased (e.g. "ZAR", "NGN", "USD").</summary>
    public string Currency { get; init; }

    /// <summary>Creates a money value, normalising the currency code to upper-case.</summary>
    public Money(decimal amount, string currency)
    {
        if (string.IsNullOrWhiteSpace(currency))
            throw new ArgumentException("Currency (ISO-4217, e.g. \"ZAR\") is required.", nameof(currency));
        Amount = amount;
        Currency = currency.Trim().ToUpperInvariant();
    }

    /// <summary>Splits into amount and currency.</summary>
    public void Deconstruct(out decimal amount, out string currency)
    {
        amount = Amount;
        currency = Currency;
    }

    /// <summary>Zero in the given currency.</summary>
    public static Money Zero(string currency) => new(0m, currency);

    /// <summary>True when the amount is exactly zero.</summary>
    public bool IsZero => Amount == 0m;

    // Guard: refuse to combine two different currencies. A default(Money) has a
    // null currency, so this also catches uninitialised operands.
    private static void EnsureSameCurrency(in Money a, in Money b)
    {
        if (!string.Equals(a.Currency, b.Currency, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Cannot combine {a.Currency ?? "<none>"} with {b.Currency ?? "<none>"}. Convert to one currency first.");
    }

    public static Money operator +(Money a, Money b) { EnsureSameCurrency(a, b); return new Money(a.Amount + b.Amount, a.Currency); }
    public static Money operator -(Money a, Money b) { EnsureSameCurrency(a, b); return new Money(a.Amount - b.Amount, a.Currency); }
    public static Money operator *(Money a, decimal factor) => new(a.Amount * factor, a.Currency);
    public static Money operator *(decimal factor, Money a) => new(a.Amount * factor, a.Currency);

    /// <summary>
    /// Rounds to <paramref name="decimals"/> places using half-away-from-zero,
    /// the conventional rule for invoice totals.
    /// </summary>
    public Money Round(int decimals = 2) => new(Math.Round(Amount, decimals, MidpointRounding.AwayFromZero), Currency);

    /// <summary>Human-readable form, e.g. "R 1 234.56". See <see cref="Currencies"/>.</summary>
    public override string ToString() => Currencies.Format(this);
}

/// <summary>
/// Small currency helper: a default currency plus display symbols for the
/// currencies CircleAI targets across 21+ countries. This is a display nicety,
/// NOT a substitute for full CLDR/ICU locale formatting — a host with real locale
/// data may present amounts however it likes. Unknown codes fall back to the
/// ISO code itself.
/// </summary>
public static class Currencies
{
    /// <summary>South Africa first, per the repo's primary market.</summary>
    public const string DefaultCurrency = "ZAR";

    private static readonly IReadOnlyDictionary<string, string> Symbols = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["ZAR"] = "R",  ["USD"] = "$",   ["EUR"] = "€", ["GBP"] = "£",
        ["NGN"] = "₦", ["KES"] = "KSh", ["GHS"] = "₵", ["TZS"] = "TSh",
        ["UGX"] = "USh", ["ZMW"] = "ZK",  ["BWP"] = "P",   ["NAD"] = "N$",
        ["MZN"] = "MT",  ["EGP"] = "E£", ["MAD"] = "DH",  ["INR"] = "₹",
    };

    /// <summary>Display symbol for a currency code, or the code itself if unknown.</summary>
    public static string SymbolFor(string currency)
    {
        if (string.IsNullOrWhiteSpace(currency)) return "";
        var code = currency.Trim().ToUpperInvariant();
        return Symbols.TryGetValue(code, out var s) ? s : code;
    }

    /// <summary>Formats money with a space thousands separator, e.g. "R 1 234.56".</summary>
    public static string Format(Money money)
    {
        var body = money.Amount.ToString("#,##0.00", CultureInfo.InvariantCulture).Replace(",", " ");
        return $"{SymbolFor(money.Currency)} {body}".Trim();
    }
}
