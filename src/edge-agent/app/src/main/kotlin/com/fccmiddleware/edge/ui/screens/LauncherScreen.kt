package com.fccmiddleware.edge.ui.screens

import android.app.ForegroundServiceStartNotAllowedException
import android.content.Intent
import android.os.Build
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.ui.platform.LocalContext
import androidx.navigation.NavHostController
import com.fccmiddleware.edge.logging.AppLogger
import com.fccmiddleware.edge.security.EncryptedPrefsManager
import com.fccmiddleware.edge.service.EdgeAgentForegroundService
import com.fccmiddleware.edge.sync.AgentCommandExecutor
import com.fccmiddleware.edge.ui.navigation.Routes
import org.koin.compose.koinInject

private const val TAG = "LauncherScreen"

@Composable
fun LauncherScreen(navController: NavHostController) {
    val context = LocalContext.current
    val encryptedPrefs: EncryptedPrefsManager = koinInject()
    val agentCommandExecutor: AgentCommandExecutor = koinInject()

    LaunchedEffect(Unit) {
        if (agentCommandExecutor.finalizeAckedResetIfNeeded("launcher")) {
            AppLogger.w(TAG, "Finalized pending reset during launcher routing")
            // Navigate to provisioning after reset
            navController.navigate(Routes.PROVISIONING) {
                popUpTo(0) { inclusive = true }
            }
            return@LaunchedEffect
        }

        val route = try {
            when {
                encryptedPrefs.isDecommissioned -> {
                    AppLogger.i(TAG, "Device is decommissioned — showing decommissioned screen")
                    Routes.DECOMMISSIONED
                }
                encryptedPrefs.isRegistered -> {
                    AppLogger.i(TAG, "Device is registered — starting service and showing site overview")
                    startForegroundServiceSafely(context)
                    Routes.SITE_OVERVIEW
                }
                else -> {
                    AppLogger.i(TAG, "Device not registered — showing provisioning screen")
                    Routes.PROVISIONING
                }
            }
        } catch (e: Exception) {
            AppLogger.e(TAG, "Failed to read registration state — routing to provisioning", e)
            Routes.PROVISIONING
        }

        navController.navigate(route) {
            popUpTo(0) { inclusive = true }
        }
    }
}

private fun startForegroundServiceSafely(context: android.content.Context) {
    try {
        context.startForegroundService(Intent(context, EdgeAgentForegroundService::class.java))
    } catch (e: Exception) {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S &&
            e is ForegroundServiceStartNotAllowedException
        ) {
            AppLogger.w(TAG, "Cannot start foreground service from background")
        } else {
            AppLogger.e(TAG, "Failed to start foreground service", e)
        }
    }
}
