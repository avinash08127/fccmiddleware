package com.fccmiddleware.edge.registration

import android.util.Base64
import com.fccmiddleware.edge.buffer.BufferDatabase
import com.fccmiddleware.edge.buffer.dao.AgentConfigDao
import com.fccmiddleware.edge.buffer.entity.AgentConfig
import com.fccmiddleware.edge.config.EdgeAgentConfigJson
import com.fccmiddleware.edge.config.SiteDataManager
import com.fccmiddleware.edge.logging.AppLogger
import com.fccmiddleware.edge.security.EncryptedPrefsManager
import com.fccmiddleware.edge.security.KeystoreManager
import com.fccmiddleware.edge.sync.CloudApiClient
import com.fccmiddleware.edge.sync.DeviceRegistrationResponse
import com.fccmiddleware.edge.sync.DeviceTokenProvider
import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import java.time.Instant

/**
 * AT-014: Extracted from ProvisioningViewModel to own the credential storage,
 * config encryption, and Room persistence pipeline for device registration.
 *
 * This class is independent of Android ViewModel/Activity lifecycle, making it
 * unit-testable without Robolectric and reusable for headless provisioning paths
 * (e.g., MDM push, API-triggered registration).
 */
class RegistrationHandler(
    private val cloudApiClient: CloudApiClient,
    private val keystoreManager: KeystoreManager,
    private val encryptedPrefs: EncryptedPrefsManager,
    private val agentConfigDao: AgentConfigDao,
    private val tokenProvider: DeviceTokenProvider,
    private val siteDataManager: SiteDataManager,
    private val bufferDatabase: BufferDatabase,
) {

    private val json = Json { ignoreUnknownKeys = true; isLenient = true }

    companion object {
        private const val TAG = "RegistrationHandler"
    }

    /**
     * Complete device registration by storing credentials, encrypting config,
     * and persisting registration state.
     *
     * @param qrCloudBaseUrl Cloud API base URL from QR code.
     * @param environment Environment identifier from QR code (nullable).
     * @param response Successful registration response from cloud.
     * @throws IllegalStateException if credential storage fails.
     */
    suspend fun completeRegistration(
        qrCloudBaseUrl: String,
        environment: String?,
        response: DeviceRegistrationResponse,
    ) {
        AppLogger.i(TAG, "Registration successful: deviceId=${response.deviceId}, site=${response.siteCode}")

        // AF-013: Clear the Room database before credentials to prevent cross-site
        // data contamination when re-provisioning for a different site.
        bufferDatabase.clearAllData()

        // Clear stale Keystore keys and EncryptedPrefs from any previous registration.
        keystoreManager.clearAll()
        encryptedPrefs.clearAll()

        val parsedSiteConfig = response.siteConfig?.let { siteConfig ->
            runCatching {
                EdgeAgentConfigJson.decode(
                    json.encodeToString(JsonObject.serializer(), siteConfig),
                )
            }.onFailure { e ->
                AppLogger.e(TAG, "Failed to parse registration siteConfig against canonical contract", e)
            }.getOrNull()
        }

        val effectiveCloudBaseUrl = parsedSiteConfig?.sync?.cloudBaseUrl ?: qrCloudBaseUrl
        cloudApiClient.updateBaseUrl(effectiveCloudBaseUrl)

        val tokensStored = tokenProvider.storeTokens(response.deviceToken, response.refreshToken)
        if (!tokensStored) {
            throw IllegalStateException(
                "Failed to store device credentials in Android Keystore. " +
                "The device cannot authenticate with the cloud without stored tokens. " +
                "Please try again or clear app data and re-provision."
            )
        }

        encryptedPrefs.saveRegistration(
            deviceId = response.deviceId,
            siteCode = response.siteCode,
            legalEntityId = response.legalEntityId,
            cloudBaseUrl = effectiveCloudBaseUrl,
            environment = environment,
        )

        parsedSiteConfig?.fcc?.hostAddress?.let { encryptedPrefs.fccHost = it }
        parsedSiteConfig?.fcc?.port?.let { encryptedPrefs.fccPort = it }

        parsedSiteConfig?.let { siteConfig ->
            val rawConfigJson = EdgeAgentConfigJson.encode(siteConfig)
            val encryptedBytes = keystoreManager.storeSecret(
                KeystoreManager.ALIAS_CONFIG_INTEGRITY, rawConfigJson
            )
            val persistedJson = if (encryptedBytes != null) {
                "ENC:" + Base64.encodeToString(encryptedBytes, Base64.NO_WRAP)
            } else {
                AppLogger.w(TAG, "Config encryption failed — persisting raw JSON")
                rawConfigJson
            }

            // AT-015: Log warning when schemaVersion fallback is triggered.
            val parsedSchemaVersion = siteConfig.schemaVersion.substringBefore(".").toIntOrNull()
            if (parsedSchemaVersion == null) {
                AppLogger.w(
                    TAG,
                    "schemaVersion '${siteConfig.schemaVersion}' could not be parsed as integer — " +
                        "falling back to 1. Check cloud config for version format mismatch.",
                )
            }

            val entity = AgentConfig(
                configJson = persistedJson,
                configVersion = siteConfig.configVersion,
                schemaVersion = parsedSchemaVersion ?: 1,
                receivedAt = Instant.now().toString(),
            )

            // AP-011: Trust Room's @Insert(onConflict = REPLACE) — if upsert()
            // doesn't throw, the write succeeded. Retry only the upsert on exception.
            var writeSucceeded = false
            for (attempt in 1..2) {
                try {
                    agentConfigDao.upsert(entity)
                    writeSucceeded = true
                    AppLogger.i(TAG, "Initial config stored in Room (encrypted, attempt=$attempt)")
                    break
                } catch (e: Exception) {
                    AppLogger.e(TAG, "Failed to store initial config (attempt=$attempt)", e)
                    if (attempt < 2) kotlinx.coroutines.delay(200)
                }
            }
            if (!writeSucceeded) {
                AppLogger.e(TAG, "Initial config could not be persisted after retries — service will fetch on first poll")
            }
        }

        parsedSiteConfig?.let { config ->
            try {
                siteDataManager.syncFromConfig(config)
            } catch (e: Exception) {
                AppLogger.e(TAG, "Failed to sync site data — will populate on first config poll", e)
            }
        }
    }
}
