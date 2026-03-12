package com.fccmiddleware.edge.sync

/**
 * Provides the device JWT and identity for authenticating cloud API calls.
 *
 * Abstracts Android Keystore / EncryptedSharedPreferences access and the
 * token refresh flow (POST /api/v1/agent/token/refresh).
 *
 * Full implementation wired in the security module (EA-2.x).
 * The CloudUploadWorker accepts a nullable provider and no-ops when unset.
 */
interface DeviceTokenProvider {

    /**
     * Returns the current device JWT bearer token, or null if the device
     * has not been provisioned yet.
     *
     * The token is a 24-hour ES256-signed JWT with claims:
     *   sub  = deviceId
     *   site = siteCode
     *   lei  = legalEntityId (Guid)
     *   roles = ["edge-agent"]
     */
    fun getAccessToken(): String?

    /**
     * Returns the Legal Entity ID bound to this device registration.
     * Extracted from the `lei` claim of the device JWT at provisioning time
     * and stored in EncryptedSharedPreferences.
     *
     * Required for the `legalEntityId` field on all uploaded transactions.
     */
    fun getLegalEntityId(): String?

    /**
     * Attempts to refresh the access token using the stored refresh token.
     *
     * Calls POST /api/v1/agent/token/refresh with the current refresh token.
     * On success, stores the new access token and updates the refresh token.
     * Returns true if a fresh access token is now available via [getAccessToken].
     *
     * Refresh tokens have a 90-day lifetime. On refresh failure (e.g. token
     * revoked or decommission) the device must be re-provisioned.
     */
    suspend fun refreshAccessToken(): Boolean

    /** True if the cloud has decommissioned this device (403 DEVICE_DECOMMISSIONED). */
    fun isDecommissioned(): Boolean

    /**
     * Persists the decommissioned flag so the device stops attempting cloud sync
     * after a 403 DEVICE_DECOMMISSIONED response.
     * Called by [CloudUploadWorker] on that specific error code.
     */
    fun markDecommissioned()

    /**
     * True if the refresh token has expired and the device needs re-provisioning.
     * Unlike decommission, re-provisioning can restore the device to active state
     * with a new bootstrap token.
     */
    fun isReprovisioningRequired(): Boolean

    /**
     * Marks the device as requiring re-provisioning. Called when the refresh
     * token has expired (401 from token refresh endpoint). Clears registration
     * state so the device routes to [ProvisioningActivity] on next startup or
     * when the foreground service detects this flag.
     */
    fun markReprovisioningRequired()

    /**
     * Stores the device token and refresh token after initial provisioning.
     * Called by [ProvisioningActivity] on successful registration.
     */
    /**
     * Returns true if both tokens were encrypted and persisted successfully.
     * Returns false if Keystore encryption failed for either token.
     */
    fun storeTokens(deviceToken: String, refreshToken: String): Boolean
}
