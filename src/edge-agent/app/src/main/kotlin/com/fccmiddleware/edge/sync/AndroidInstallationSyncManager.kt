package com.fccmiddleware.edge.sync

import android.content.Context
import android.os.Build
import com.fccmiddleware.edge.logging.AppLogger
import com.fccmiddleware.edge.push.FirebasePushAvailability
import com.fccmiddleware.edge.push.FirebasePushClient
import com.fccmiddleware.edge.security.EncryptedPrefsManager
import java.security.MessageDigest

sealed class AndroidInstallationSyncResult {
    data object Synced : AndroidInstallationSyncResult()
    data object Unchanged : AndroidInstallationSyncResult()
    data class FirebaseUnavailable(val reason: String) : AndroidInstallationSyncResult()
    data class PendingRetry(val reason: String) : AndroidInstallationSyncResult()
    data object Decommissioned : AndroidInstallationSyncResult()
    data object NotRegistered : AndroidInstallationSyncResult()
}

class AndroidInstallationSyncManager(
    private val context: Context,
    private val encryptedPrefs: EncryptedPrefsManager,
    private val cloudApiClient: CloudApiClient,
    private val tokenProvider: DeviceTokenProvider,
    private val firebasePushClient: FirebasePushClient,
) {

    companion object {
        private const val TAG = "InstallSyncManager"
    }

    suspend fun syncCurrentInstallation(
        reason: String,
        force: Boolean = false,
        tokenOverride: String? = null,
    ): AndroidInstallationSyncResult {
        if (!encryptedPrefs.isRegistered || encryptedPrefs.isReprovisioningRequired) {
            encryptedPrefs.isAndroidInstallationSyncPending = false
            return AndroidInstallationSyncResult.NotRegistered
        }

        if (tokenProvider.isDecommissioned() || encryptedPrefs.isDecommissioned) {
            encryptedPrefs.isAndroidInstallationSyncPending = false
            return AndroidInstallationSyncResult.Decommissioned
        }

        val availability = firebasePushClient.availability()
        if (availability is FirebasePushAvailability.Unavailable && tokenOverride.isNullOrBlank()) {
            encryptedPrefs.isAndroidInstallationSyncPending = false
            AppLogger.i(TAG, "Android installation sync skipped: ${availability.reason}")
            return AndroidInstallationSyncResult.FirebaseUnavailable(availability.reason)
        }

        val registrationToken = tokenOverride?.takeIf { it.isNotBlank() }
            ?: firebasePushClient.getCurrentToken()
            ?: return retryLater("firebase_token_unavailable")

        val tokenHash = sha256Hex(registrationToken)
        val lastHash = encryptedPrefs.lastSyncedAndroidInstallationTokenHash
        val needsNetworkSync = force ||
            encryptedPrefs.isAndroidInstallationSyncPending ||
            lastHash == null ||
            lastHash != tokenHash

        if (!needsNetworkSync) {
            AppLogger.d(TAG, "Android installation token already synced (reason=$reason)")
            return AndroidInstallationSyncResult.Unchanged
        }

        encryptedPrefs.isAndroidInstallationSyncPending = true

        val bearerToken = tokenProvider.getAccessToken()
            ?: return retryLater("device_access_token_unavailable")

        val request = AndroidInstallationUpsertRequest(
            installationId = encryptedPrefs.getOrCreateAndroidInstallationId(),
            registrationToken = registrationToken,
            appVersion = context.packageManager
                .getPackageInfo(context.packageName, 0).versionName ?: "1.0.0",
            osVersion = Build.VERSION.RELEASE ?: "unknown",
            deviceModel = Build.MODEL ?: "unknown",
        )

        return when (val result = upsertOnce(request, bearerToken)) {
            is CloudInstallationUpsertResult.Success -> {
                encryptedPrefs.lastSyncedAndroidInstallationTokenHash = tokenHash
                encryptedPrefs.isAndroidInstallationSyncPending = false
                AppLogger.i(TAG, "Android installation token synced (reason=$reason)")
                AndroidInstallationSyncResult.Synced
            }

            is CloudInstallationUpsertResult.Unauthorized -> {
                if (!tokenProvider.refreshAccessToken()) {
                    retryLater("installation_upsert_401_refresh_failed")
                } else {
                    val freshToken = tokenProvider.getAccessToken()
                        ?: return retryLater("installation_upsert_refreshed_token_missing")
                    when (val retry = upsertOnce(request, freshToken)) {
                        is CloudInstallationUpsertResult.Success -> {
                            encryptedPrefs.lastSyncedAndroidInstallationTokenHash = tokenHash
                            encryptedPrefs.isAndroidInstallationSyncPending = false
                            AppLogger.i(TAG, "Android installation token synced after refresh (reason=$reason)")
                            AndroidInstallationSyncResult.Synced
                        }

                        is CloudInstallationUpsertResult.Forbidden -> handleForbidden(retry.errorCode)
                        is CloudInstallationUpsertResult.Unauthorized ->
                            retryLater("installation_upsert_401_after_refresh")
                        is CloudInstallationUpsertResult.NotFound -> {
                            AppLogger.w(TAG, "Installation upsert 404 after refresh: ${retry.errorCode} — feature disabled, stopping retries")
                            encryptedPrefs.isAndroidInstallationSyncPending = false
                            AndroidInstallationSyncResult.Synced // Stop retrying permanently
                        }
                        is CloudInstallationUpsertResult.Conflict -> {
                            AppLogger.e(TAG, "Installation upsert 409 after refresh: ${retry.errorCode} — ownership conflict, stopping retries")
                            encryptedPrefs.isAndroidInstallationSyncPending = false
                            AndroidInstallationSyncResult.Synced // Stop retrying permanently
                        }
                        is CloudInstallationUpsertResult.TransportError ->
                            retryLater(retry.message)
                    }
                }
            }

            is CloudInstallationUpsertResult.Forbidden -> handleForbidden(result.errorCode)
            is CloudInstallationUpsertResult.NotFound -> {
                AppLogger.w(TAG, "Installation upsert 404: ${result.errorCode} — feature disabled, stopping retries")
                encryptedPrefs.isAndroidInstallationSyncPending = false
                AndroidInstallationSyncResult.Synced
            }
            is CloudInstallationUpsertResult.Conflict -> {
                AppLogger.e(TAG, "Installation upsert 409: ${result.errorCode} — ownership conflict, stopping retries")
                encryptedPrefs.isAndroidInstallationSyncPending = false
                AndroidInstallationSyncResult.Synced
            }
            is CloudInstallationUpsertResult.TransportError -> retryLater(result.message)
        }
    }

    private suspend fun upsertOnce(
        request: AndroidInstallationUpsertRequest,
        bearerToken: String,
    ): CloudInstallationUpsertResult = cloudApiClient.upsertAndroidInstallation(request, bearerToken)

    private fun handleForbidden(errorCode: String?): AndroidInstallationSyncResult {
        return if (errorCode == "DEVICE_DECOMMISSIONED") {
            tokenProvider.markDecommissioned()
            encryptedPrefs.isAndroidInstallationSyncPending = false
            AndroidInstallationSyncResult.Decommissioned
        } else {
            retryLater("installation_upsert_forbidden:${errorCode ?: "unknown"}")
        }
    }

    private fun retryLater(reason: String): AndroidInstallationSyncResult {
        encryptedPrefs.isAndroidInstallationSyncPending = true
        AppLogger.w(TAG, "Android installation sync deferred: $reason")
        return AndroidInstallationSyncResult.PendingRetry(reason)
    }

    private fun sha256Hex(value: String): String {
        val digest = MessageDigest.getInstance("SHA-256").digest(value.toByteArray(Charsets.UTF_8))
        return digest.joinToString(separator = "") { byte -> "%02x".format(byte) }
    }
}
