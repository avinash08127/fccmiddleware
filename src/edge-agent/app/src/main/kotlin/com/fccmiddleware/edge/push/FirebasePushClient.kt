package com.fccmiddleware.edge.push

import android.content.Context
import android.content.pm.PackageManager
import android.os.Build
import com.fccmiddleware.edge.BuildConfig
import com.fccmiddleware.edge.logging.AppLogger
import com.google.firebase.FirebaseApp
import com.google.firebase.FirebaseOptions
import com.google.firebase.messaging.FirebaseMessaging
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.tasks.await

sealed class FirebasePushAvailability {
    data object Available : FirebasePushAvailability()
    data class Unavailable(val reason: String) : FirebasePushAvailability()
}

/**
 * Best-effort Firebase client.
 *
 * The agent must keep working even when the field image lacks Play Services or
 * the app has no bundled Firebase config. In those cases this client reports a
 * concrete unavailable reason and callers are expected to no-op.
 */
class FirebasePushClient(
    private val context: Context,
) {

    companion object {
        private const val TAG = "FirebasePushClient"
    }

    fun availability(): FirebasePushAvailability {
        val app = ensureFirebaseApp()
            ?: return FirebasePushAvailability.Unavailable("firebase_not_configured")

        if (!hasGooglePlayServicesPackage()) {
            return FirebasePushAvailability.Unavailable(
                "google_play_services_package_missing",
            )
        }

        return if (app.options.applicationId.isNullOrBlank()) {
            FirebasePushAvailability.Unavailable("firebase_application_id_missing")
        } else {
            FirebasePushAvailability.Available
        }
    }

    suspend fun getCurrentToken(): String? {
        val availability = availability()
        if (availability is FirebasePushAvailability.Unavailable) {
            AppLogger.i(TAG, "Firebase token fetch skipped: ${availability.reason}")
            return null
        }

        return try {
            FirebaseMessaging.getInstance().token.await().takeIf { !it.isNullOrBlank() }
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            AppLogger.w(TAG, "Failed to obtain Firebase registration token: ${e.message}")
            null
        }
    }

    private fun ensureFirebaseApp(): FirebaseApp? {
        FirebaseApp.getApps(context).firstOrNull()?.let { return it }

        runCatching { FirebaseApp.initializeApp(context) }
            .getOrNull()
            ?.let { return it }

        val manualOptions = manualOptions() ?: return null
        return runCatching { FirebaseApp.initializeApp(context, manualOptions) }
            .onFailure { e ->
                AppLogger.w(TAG, "Manual Firebase initialization failed: ${e.message}")
            }
            .getOrNull()
    }

    private fun manualOptions(): FirebaseOptions? {
        val applicationId = BuildConfig.FCM_SPIKE_APPLICATION_ID.trim()
        val projectId = BuildConfig.FCM_SPIKE_PROJECT_ID.trim()
        val apiKey = BuildConfig.FCM_SPIKE_API_KEY.trim()
        val senderId = BuildConfig.FCM_SPIKE_SENDER_ID.trim()

        if (
            applicationId.isBlank() ||
            projectId.isBlank() ||
            apiKey.isBlank() ||
            senderId.isBlank()
        ) {
            return null
        }

        return FirebaseOptions.Builder()
            .setApplicationId(applicationId)
            .setProjectId(projectId)
            .setApiKey(apiKey)
            .setGcmSenderId(senderId)
            .build()
    }

    private fun hasGooglePlayServicesPackage(): Boolean {
        return try {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
                context.packageManager.getPackageInfo(
                    "com.google.android.gms",
                    PackageManager.PackageInfoFlags.of(0),
                )
            } else {
                @Suppress("DEPRECATION")
                context.packageManager.getPackageInfo("com.google.android.gms", 0)
            }
            true
        } catch (_: Exception) {
            false
        }
    }
}
