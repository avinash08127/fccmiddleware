namespace FccMiddleware.Domain.Common;

/// <summary>
/// Centralized currency-to-minor-unit factor mapping.
/// All cloud adapters MUST use this instead of per-adapter mappings
/// to prevent 100x monetary errors from inconsistent factor tables.
/// </summary>
public static class CurrencyHelper
{
    /// <summary>
    /// Returns the multiplier that converts a major-unit amount to minor units
    /// for the given ISO 4217 currency code.
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

            // 2-decimal currencies (default — USD, EUR, GBP, ZAR, KES, etc.)
            _ => 100m
        };
    }
}
