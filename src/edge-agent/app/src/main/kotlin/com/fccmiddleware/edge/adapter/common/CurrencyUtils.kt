package com.fccmiddleware.edge.adapter.common

import kotlin.math.pow

/**
 * ISO 4217 currency decimal-place lookup shared across adapters and WebSocket mapping.
 *
 * Used to convert between minor units (stored in DB) and major units (sent to POS).
 * The factor is 10^decimalPlaces: TZS(0)=1, USD(2)=100, KWD(3)=1000.
 */
object CurrencyUtils {

    /**
     * Returns the number of decimal places for the given ISO 4217 currency code.
     * Defaults to 2 for unrecognized currencies.
     */
    fun getDecimalPlaces(currencyCode: String): Int = when (currencyCode.uppercase()) {
        "BHD", "IQD", "JOD", "KWD", "LYD", "OMR", "TND" -> 3
        "BIF", "CLP", "DJF", "GNF", "ISK", "JPY", "KMF",
        "KRW", "PYG", "RWF", "TZS", "UGX", "UYI", "VND",
        "VUV", "XAF", "XOF", "XPF" -> 0
        else -> 2
    }

    /**
     * Returns the currency factor (10^decimalPlaces) for converting between
     * minor and major units.
     *
     * Examples: TZS -> 1.0, USD -> 100.0, KWD -> 1000.0
     */
    fun getFactor(currencyCode: String): Double {
        return 10.0.pow(getDecimalPlaces(currencyCode))
    }
}
