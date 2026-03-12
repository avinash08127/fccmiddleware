package com.fccmiddleware.edge.adapter.radix

import kotlinx.serialization.Serializable

// ---------------------------------------------------------------------------
// Radix FCC protocol DTOs — XML request/response data classes.
//
// Radix uses HTTP POST with XML bodies on dual ports:
//   Port P   : External authorization (pre-auth)
//   Port P+1 : Transaction management, products, mode changes
//
// All decimal values (amo, vol, price) remain as Strings in DTOs.
// Conversion to Long (microlitres, minor units) happens during normalization.
// Date/time fields remain as Strings — parsing happens during normalization.
// ---------------------------------------------------------------------------

// ===== Transaction data (parsed from <TRN> element) =====

/**
 * All attributes from a Radix `<TRN>` XML element.
 *
 * Field names follow the Radix spec §2.4 mapped to Kotlin camelCase.
 * Decimal string fields (amo, vol, price) are kept as String to avoid
 * premature floating-point conversion — normalization converts them
 * to Long microlitres / minor units via BigDecimal.
 */
@Serializable
data class RadixTransactionData(
    /** Amount in local currency (decimal string, e.g. "30000.0"). */
    val amo: String,
    /** Electronic Fiscal Device receipt ID (e.g. "182AC9368989"). */
    val efdId: String,
    /** FDC local date (e.g. "2021-03-03"). */
    val fdcDate: String,
    /** FDC local time (e.g. "21:17:53"). */
    val fdcTime: String,
    /** FDC station name (e.g. "10TZ100449"). */
    val fdcName: String,
    /** FDC serial number — part of dedup key (e.g. "100253410"). */
    val fdcNum: String,
    /** Product number (FCC internal index, e.g. "0"). */
    val fdcProd: String,
    /** Product display name (e.g. "UNLEADED"). */
    val fdcProdName: String,
    /** Transaction save number — part of dedup key (e.g. "368989"). */
    val fdcSaveNum: String,
    /** Tank reference (may be empty). */
    val fdcTank: String,
    /** Filling point within the DSB/RDG (e.g. "0"). */
    val fp: String,
    /** Nozzle number within the filling point (e.g. "0"). */
    val noz: String,
    /** Unit price (decimal string, e.g. "1930"). */
    val price: String,
    /** DSB/RDG unit address (e.g. "0"). */
    val pumpAddr: String,
    /** Register date (e.g. "2021-03-03"). */
    val rdgDate: String,
    /** Register time (e.g. "21:17:53"). */
    val rdgTime: String,
    /** Register ID (e.g. "0"). */
    val rdgId: String,
    /** Register index (e.g. "0"). */
    val rdgIndex: String,
    /** Register product (e.g. "0"). */
    val rdgProd: String,
    /** RDG-level save number (e.g. "1066"). */
    val rdgSaveNum: String,
    /** Site tax registration ID (e.g. "TZ0100551361"). */
    val regId: String,
    /** Rounding type (e.g. "0"). */
    val roundType: String,
    /** Volume in litres (decimal string, e.g. "15.54"). */
    val vol: String,
)

// ===== RFID Card data (parsed from <RFID_CARD> element) =====

/**
 * Attributes from a Radix `<RFID_CARD>` XML element.
 *
 * Present in transaction responses when an RFID card was used.
 * See Radix spec §2.11.
 */
@Serializable
data class RadixRfidCardData(
    /** Card type identifier. */
    val cardType: String,
    /** Customer contact info. */
    val custContact: String,
    /** Customer ID value. */
    val custId: String,
    /** Customer ID type (1=TIN, 2=DL, 3=Voter, 4=Passport, 5=NID, 6=NIL). */
    val custIdType: String,
    /** Customer name. */
    val custName: String,
    /** Discount applied via card. */
    val discount: String,
    /** Discount type (e.g. "PERCENT", "VALUE"). */
    val discountType: String,
    /** Card number. */
    val num: String,
    /** Card number base-10 representation. */
    val num10: String,
    /** Payment method. */
    val payMethod: String,
    /** Product enabled flag. */
    val productEnabled: String,
    /** Whether the RFID card was used (0=no, 1=yes). */
    val used: String,
)

// ===== Discount data (parsed from <DISCOUNT> element) =====

/**
 * Attributes from a Radix `<DISCOUNT>` XML element.
 *
 * Present in transaction responses when a discount was applied.
 * See Radix spec §2.11. All monetary/volume fields are decimal strings.
 */
@Serializable
data class RadixDiscountData(
    /** Discount amount (decimal string). */
    val amoDiscount: String,
    /** New amount after discount (decimal string). */
    val amoNew: String,
    /** Original amount before discount (decimal string). */
    val amoOrigin: String,
    /** Discount type (e.g. "PERCENT", "VALUE"). */
    val discountType: String,
    /** Price discount (decimal string). */
    val priceDiscount: String,
    /** New price after discount (decimal string). */
    val priceNew: String,
    /** Original price before discount (decimal string). */
    val priceOrigin: String,
    /** Original volume (decimal string). */
    val volOrigin: String,
)

// ===== Customer data (parsed from <CUST_DATA> element) =====

/**
 * Attributes from a Radix `<CUST_DATA>` XML element.
 *
 * When USED=1, pre-auth customer data may be echoed back.
 */
@Serializable
data class RadixCustomerData(
    /** Whether customer data was used (0=no, 1=yes). */
    val used: Int,
)

// ===== Response envelopes =====

/**
 * Parsed `<FDC_RESP>` envelope — response to transaction fetch (CMD_CODE=10)
 * or unsolicited push (RESP_CODE=30).
 *
 * Contains the ANS element (respCode, respMsg, token) plus optional
 * TRN, RFID_CARD, DISCOUNT, and CUST_DATA child elements.
 */
@Serializable
data class RadixTransactionResponse(
    /** Response code from `<ANS RESP_CODE="...">`. 201=success, 205=no transaction, 30=unsolicited. */
    val respCode: Int,
    /** Response message from `<ANS RESP_MSG="...">`. */
    val respMsg: String,
    /** Echoed token from `<ANS TOKEN="...">`. */
    val token: String,
    /** Parsed `<TRN>` element. Null when RESP_CODE=205 (no transaction). */
    val transaction: RadixTransactionData? = null,
    /** Parsed `<RFID_CARD>` element. Null when not present or empty. */
    val rfidCard: RadixRfidCardData? = null,
    /** Parsed `<DISCOUNT>` element. Null when not present or empty. */
    val discount: RadixDiscountData? = null,
    /** Parsed `<CUST_DATA>` element. Null when not present. */
    val customerData: RadixCustomerData? = null,
    /** SHA-1 signature from `<SIGNATURE>` element for verification. */
    val signature: String,
)

/**
 * Parsed `<FDCMS><FDCACK>` envelope — response to pre-auth (AUTH_DATA)
 * commands on the authorization port P.
 */
@Serializable
data class RadixAuthResponse(
    /** FDC acknowledgment date (e.g. "2021-03-01"). */
    val date: String,
    /** FDC acknowledgment time (e.g. "09:38:42"). */
    val time: String,
    /** Acknowledgment code. 0=SUCCESS, 251=SIGNATURE_ERR, 255=BAD_XML, etc. */
    val ackCode: Int,
    /** Acknowledgment message (e.g. "Success"). */
    val ackMsg: String,
    /** SHA-1 signature from `<FDCSIGNATURE>` element for verification. */
    val signature: String,
)

/**
 * Single product item parsed from a CMD_CODE=55 (read products) response.
 */
@Serializable
data class RadixProductData(
    /** Product ID (FCC internal index). */
    val id: Int,
    /** Product display name (e.g. "UNLEADED"). */
    val name: String,
    /** Unit price (decimal string). */
    val price: String,
)

/**
 * Parsed response to CMD_CODE=55 (read products/prices).
 *
 * Also used as the heartbeat/liveness probe — a successful response
 * (respCode=201) confirms FDC is responsive.
 */
@Serializable
data class RadixProductResponse(
    /** Response code. 201=success. */
    val respCode: Int,
    /** Response message. */
    val respMsg: String,
    /** List of products returned by the FDC. */
    val products: List<RadixProductData>,
)

// ===== Request parameters (builder input for XML construction) =====

/**
 * Parameters for building a pre-auth `<AUTH_DATA>` XML request.
 *
 * Sent to the authorization port P. All fields map directly to
 * AUTH_DATA child elements per Radix spec §2.6.
 */
@Serializable
data class RadixPreAuthParams(
    /** DSB/RDG unit number (from pump address mapping). */
    val pump: Int,
    /** Filling point within DSB/RDG (from pump address mapping). */
    val fp: Int,
    /** true=authorize, false=cancel. Maps to `<AUTH>TRUE</AUTH>` / `<AUTH>FALSE</AUTH>`. */
    val authorize: Boolean,
    /** Product number (FCC internal index, 0=all products). */
    val product: Int,
    /** Volume preset in litres (decimal string, "0.00" = not used). */
    val presetVolume: String,
    /** Amount preset in local currency (decimal string). */
    val presetAmount: String,
    /** Optional: customer/company name for fiscal data. */
    val customerName: String? = null,
    /** Optional: customer ID type (1=TIN, 2=DL, 3=Voter, 4=Passport, 5=NID, 6=NIL). */
    val customerIdType: Int? = null,
    /** Optional: customer ID value. */
    val customerId: String? = null,
    /** Optional: customer phone number. */
    val mobileNumber: String? = null,
    /** Optional: discount value. */
    val discountValue: Int? = null,
    /** Optional: discount type ("PERCENT" or "VALUE"). */
    val discountType: String? = null,
    /** Correlation token (0–65535), echoed in the resulting dispense transaction. */
    val token: String,
)

/**
 * Parameters for building a mode change `CMD_CODE=20` XML request.
 *
 * Sent to the transaction port P+1.
 */
@Serializable
data class RadixModeChangeParams(
    /** Transaction mode: 0=OFF, 1=ON_DEMAND (pull), 2=UNSOLICITED (push). */
    val mode: Int,
    /** Request token for correlation. */
    val token: String,
)
