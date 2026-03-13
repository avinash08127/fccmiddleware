namespace FccDesktopAgent.Core.Adapter.Common;

/// <summary>
/// Centralized currency-to-minor-unit factor mapping.
/// All desktop adapters MUST use this instead of per-adapter mappings
/// to prevent monetary errors from inconsistent factor tables.
/// </summary>
public static class CurrencyHelper
{
    /// <summary>
    /// Returns the multiplier that converts a major-unit amount to minor units
    /// for the given ISO 4217 currency code.
    /// Throws <see cref="ArgumentException"/> for unknown currencies to prevent
    /// silent 10x–100x pricing errors.
    /// </summary>
    public static decimal GetCurrencyFactor(string currencyCode)
    {
        return currencyCode.ToUpperInvariant() switch
        {
            // 3-decimal currencies
            "KWD" or "BHD" or "OMR" => 1000m,

            // 0-decimal currencies
            "JPY" or "KRW" or "VND"                     // East Asia
                or "TZS" or "UGX" or "RWF" or "GNF"     // East/West Africa
                or "PYG" or "CLP"                         // Latin America
                or "ISK"                                  // Europe
                => 1m,

            // 2-decimal currencies — explicitly listed to reject unknowns
            "USD" or "EUR" or "GBP" or "ZAR" or "KES" or "NGN"
                or "GHS" or "MWK" or "MZN" or "ZMW" or "BWP"
                or "AED" or "SAR" or "QAR" or "INR" or "CNY"
                or "BRL" or "CAD" or "AUD" or "NZD" or "SGD"
                or "HKD" or "MYR" or "THB" or "PHP" or "IDR"
                or "EGP" or "MAD" or "TND" or "ETB" or "AOA"
                or "CDF" or "XOF" or "XAF" or "SOS" or "SDG"
                or "SSP" or "ERN" or "DJF" or "GMD" or "SLL"
                or "LRD" or "SZL" or "LSL" or "NAD" or "SCR"
                or "MUR" or "MVR" or "BDT" or "PKR" or "LKR"
                or "NPR" or "MMK" or "KHR" or "LAK" or "MNT"
                or "KZT" or "UZS" or "GEL" or "AMD" or "AZN"
                or "TRY" or "RUB" or "UAH" or "PLN" or "CZK"
                or "HUF" or "RON" or "BGN" or "HRK" or "RSD"
                or "MKD" or "ALL" or "BAM" or "MDL" or "SEK"
                or "NOK" or "DKK" or "CHF" or "ILS" or "JOD"
                or "LBP" or "IQD" or "IRR" or "AFN" or "SYP"
                or "YER" or "MXN" or "ARS" or "COP" or "PEN"
                or "VES" or "UYU" or "BOB" or "GTQ" or "HNL"
                or "NIO" or "CRC" or "DOP" or "JMD" or "TTD"
                or "BBD" or "BZD" or "GYD" or "SRD" or "HTG"
                or "PAB" or "CUP" or "FJD" or "PGK" or "WST"
                or "TOP" or "VUV" or "SBD" or "TWD"
                => 100m,

            _ => throw new ArgumentException(
                $"Unknown currency code '{currencyCode}'. Add it to CurrencyHelper to prevent " +
                "silent monetary conversion errors.",
                nameof(currencyCode))
        };
    }

    /// <summary>
    /// Returns the currency factor, falling back to <paramref name="fallbackFactor"/>
    /// for unknown currencies (with logging responsibility on the caller).
    /// Use this only when rejecting the transaction is not an option.
    /// </summary>
    public static decimal GetCurrencyFactorOrDefault(string currencyCode, decimal fallbackFactor = 100m)
    {
        try
        {
            return GetCurrencyFactor(currencyCode);
        }
        catch (ArgumentException)
        {
            return fallbackFactor;
        }
    }
}
