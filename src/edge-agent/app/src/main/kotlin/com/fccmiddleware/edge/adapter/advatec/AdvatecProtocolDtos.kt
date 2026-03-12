@file:UseSerializers(BigDecimalSerializer::class)

package com.fccmiddleware.edge.adapter.advatec

import kotlinx.serialization.KSerializer
import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable
import kotlinx.serialization.UseSerializers
import kotlinx.serialization.descriptors.PrimitiveKind
import kotlinx.serialization.descriptors.PrimitiveSerialDescriptor
import kotlinx.serialization.descriptors.SerialDescriptor
import kotlinx.serialization.encoding.Decoder
import kotlinx.serialization.encoding.Encoder
import kotlinx.serialization.json.JsonDecoder
import kotlinx.serialization.json.JsonPrimitive
import java.math.BigDecimal

/**
 * Custom kotlinx.serialization serializer for [BigDecimal].
 *
 * Reads the raw JSON number token via [JsonPrimitive.content] to avoid
 * Double intermediate representation and the precision loss that entails.
 * Monetary and volume values must never pass through floating-point.
 */
object BigDecimalSerializer : KSerializer<BigDecimal> {
    override val descriptor: SerialDescriptor =
        PrimitiveSerialDescriptor("java.math.BigDecimal", PrimitiveKind.DOUBLE)

    override fun serialize(encoder: Encoder, value: BigDecimal) {
        encoder.encodeDouble(value.toDouble())
    }

    override fun deserialize(decoder: Decoder): BigDecimal {
        if (decoder is JsonDecoder) {
            val element = decoder.decodeJsonElement()
            if (element is JsonPrimitive) {
                return BigDecimal(element.content)
            }
        }
        return BigDecimal(decoder.decodeDouble().toString())
    }
}

// ── Webhook envelope ────────────────────────────────────────────────────────

/**
 * Top-level Advatec webhook envelope. DataType is either "Receipt" or "Customer".
 * Advatec uses PascalCase for all JSON field names.
 */
@Serializable
data class AdvatecWebhookEnvelope(
    @SerialName("DataType") val dataType: String,
    @SerialName("Data") val data: AdvatecReceiptData? = null,
)

// ── Receipt DTOs ────────────────────────────────────────────────────────────

/**
 * Advatec Receipt webhook Data payload.
 * Contains full TRA fiscal receipt including tax breakdown, payment methods,
 * and TRA verification URL. This is the richest transaction payload of any vendor.
 */
@Serializable
data class AdvatecReceiptData(
    @SerialName("Date") val date: String? = null,
    @SerialName("Time") val time: String? = null,
    @SerialName("ZNumber") val zNumber: Long? = null,
    @SerialName("ReceiptCode") val receiptCode: String? = null,
    @SerialName("TransactionId") val transactionId: String? = null,
    @SerialName("CustomerIdType") val customerIdType: Int? = null,
    @SerialName("CustomerIdType_") val customerIdTypeName: String? = null,
    @SerialName("CustomerId") val customerId: String? = null,
    @SerialName("CustomerName") val customerName: String? = null,
    @SerialName("CustomerPhone") val customerPhone: String? = null,
    @SerialName("TotalDiscountAmount") val totalDiscountAmount: BigDecimal? = null,
    @SerialName("DailyCount") val dailyCount: Int? = null,
    @SerialName("GlobalCount") val globalCount: Long? = null,
    @SerialName("ReceiptNumber") val receiptNumber: Long? = null,
    @SerialName("AmountInclusive") val amountInclusive: BigDecimal = BigDecimal.ZERO,
    @SerialName("AmountExclusive") val amountExclusive: BigDecimal? = null,
    @SerialName("TotalTaxAmount") val totalTaxAmount: BigDecimal? = null,
    @SerialName("AmountPaid") val amountPaid: BigDecimal? = null,
    @SerialName("Items") val items: List<AdvatecReceiptItem>? = null,
    @SerialName("Company") val company: AdvatecCompanyInfo? = null,
    @SerialName("Payments") val payments: List<AdvatecPaymentItem>? = null,
    @SerialName("ReceiptVCodeURL") val receiptVCodeUrl: String? = null,
)

/**
 * Individual line item on an Advatec TRA fiscal receipt.
 * Typically a single fuel product for fuel station transactions.
 */
@Serializable
data class AdvatecReceiptItem(
    @SerialName("Price") val price: BigDecimal = BigDecimal.ZERO,
    @SerialName("Amount") val amount: BigDecimal = BigDecimal.ZERO,
    @SerialName("TaxCode") val taxCode: String? = null,
    @SerialName("Quantity") val quantity: BigDecimal = BigDecimal.ZERO,
    @SerialName("TaxAmount") val taxAmount: BigDecimal? = null,
    @SerialName("Product") val product: String? = null,
    @SerialName("TaxId") val taxId: Int? = null,
    @SerialName("DiscountAmount") val discountAmount: BigDecimal? = null,
    @SerialName("TaxRate") val taxRate: BigDecimal? = null,
)

/**
 * Advatec Company/operator information registered with TRA.
 * Static per site — can be validated against site configuration.
 */
@Serializable
data class AdvatecCompanyInfo(
    @SerialName("TIN") val tin: String? = null,
    @SerialName("VRN") val vrn: String? = null,
    @SerialName("City") val city: String? = null,
    @SerialName("Region") val region: String? = null,
    @SerialName("Mobile") val mobile: String? = null,
    @SerialName("Street") val street: String? = null,
    @SerialName("Country") val country: String? = null,
    @SerialName("TaxOffice") val taxOffice: String? = null,
    @SerialName("SerialNumber") val serialNumber: String? = null,
    @SerialName("RegistrationId") val registrationId: String? = null,
    @SerialName("UIN") val uin: String? = null,
    @SerialName("Name") val name: String? = null,
)

/**
 * Payment method entry supporting split payments.
 * Advatec is the only FCC vendor with multi-payment support.
 * Types: CASH, CCARD, EMONEY, INVOICE, CHEQUE.
 */
@Serializable
data class AdvatecPaymentItem(
    @SerialName("PaymentType") val paymentType: String? = null,
    @SerialName("PaymentAmount") val paymentAmount: BigDecimal? = null,
)

// ── Customer submission DTOs ────────────────────────────────────────────────

/**
 * Customer data submission request (Edge Agent -> Advatec).
 * POST http://{host}:{port}/api/v2/incoming
 * Used for post-dispense fiscalization or pre-auth trigger (pending AQ-1).
 */
@Serializable
data class AdvatecCustomerRequest(
    @SerialName("DataType") val dataType: String = "Customer",
    @SerialName("Data") val data: AdvatecCustomerData,
)

@Serializable
data class AdvatecCustomerData(
    @SerialName("Pump") val pump: Int,
    @SerialName("Dose") val dose: BigDecimal,
    /** TRA CustIdType: 1=TIN, 2=DL, 3=Voters, 4=Passport, 5=NID, 6=NIL. */
    @SerialName("CustIdType") val custIdType: Int = 6,
    @SerialName("CustomerId") val customerId: String = "",
    @SerialName("CustomerName") val customerName: String = "",
    @SerialName("Payments") val payments: List<AdvatecPaymentItem> = emptyList(),
)
