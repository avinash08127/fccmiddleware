package com.fccmiddleware.edge.ui

import com.fccmiddleware.edge.security.Sensitive

/** Parsed QR code bootstrap data. */
data class QrBootstrapData(
    val siteCode: String,
    val cloudBaseUrl: String,
    @Sensitive val provisioningToken: String,
    val environment: String? = null,
) {
    // S-007: redact the token so it can never appear in a log line even if the
    // object is accidentally passed to AppLogger or another logging call.
    override fun toString(): String =
        "QrBootstrapData(siteCode=$siteCode, cloudBaseUrl=$cloudBaseUrl, " +
            "provisioningToken=***, environment=$environment)"
}
