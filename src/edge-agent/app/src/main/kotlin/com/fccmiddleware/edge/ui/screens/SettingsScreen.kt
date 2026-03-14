package com.fccmiddleware.edge.ui.screens

import android.os.SystemClock
import android.view.WindowManager
import android.widget.Toast
import androidx.activity.ComponentActivity
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableLongStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.navigation.NavHostController
import com.fccmiddleware.edge.config.ConfigManager
import com.fccmiddleware.edge.config.LocalOverrideManager
import com.fccmiddleware.edge.buffer.BufferDatabase
import com.fccmiddleware.edge.logging.AppLogger
import com.fccmiddleware.edge.runtime.CadenceController
import com.fccmiddleware.edge.security.EncryptedPrefsManager
import com.fccmiddleware.edge.security.KeystoreManager
import com.fccmiddleware.edge.ui.ReprovisionHelper
import com.fccmiddleware.edge.ui.navigation.Routes
import com.fccmiddleware.edge.ui.theme.DataRow
import com.fccmiddleware.edge.ui.theme.MonoDataRow
import com.fccmiddleware.edge.ui.theme.PumaButton
import com.fccmiddleware.edge.ui.theme.PumaGreen
import com.fccmiddleware.edge.ui.theme.PumaOutlinedButton
import com.fccmiddleware.edge.ui.theme.PumaRed
import com.fccmiddleware.edge.ui.theme.SectionHeader
import com.fccmiddleware.edge.ui.theme.StatusGreen
import com.fccmiddleware.edge.ui.theme.StatusRed
import com.fccmiddleware.edge.ui.theme.TextGray
import com.fccmiddleware.edge.ui.theme.TextLabel
import com.fccmiddleware.edge.ui.theme.TextOverride
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import org.koin.compose.koinInject

private const val TAG = "SettingsScreen"
private const val DEBOUNCE_MS = 1000L

private val CLOUD_API_ROUTES = listOf(
    "Registration" to "/api/v1/agent/register",
    "Config Poll" to "/api/v1/agent/config",
    "Token Refresh" to "/api/v1/agent/token/refresh",
    "Transaction Upload" to "/api/v1/transactions/upload",
    "Synced Status" to "/api/v1/transactions/synced-status",
    "Pre-Auth Forward" to "/api/v1/preauth",
    "Telemetry" to "/api/v1/agent/telemetry",
    "Diagnostic Logs" to "/api/v1/agent/diagnostic-logs",
    "Version Check" to "/api/v1/agent/version-check",
)

@Composable
fun SettingsScreen(navController: NavHostController) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    val localOverrideManager: LocalOverrideManager = koinInject()
    val configManager: ConfigManager = koinInject()
    val encryptedPrefs: EncryptedPrefsManager = koinInject()
    val cadenceController: CadenceController = koinInject()
    val bufferDatabase: BufferDatabase = koinInject()
    val keystoreManager: KeystoreManager = koinInject()

    // FLAG_SECURE — prevent screenshots on settings screen
    DisposableEffect(Unit) {
        val activity = context as ComponentActivity
        activity.window.setFlags(WindowManager.LayoutParams.FLAG_SECURE, WindowManager.LayoutParams.FLAG_SECURE)
        onDispose { activity.window.clearFlags(WindowManager.LayoutParams.FLAG_SECURE) }
    }

    // Editable fields — saved across rotation (except access code per AF-001/LR-012)
    var fccIp by rememberSaveable { mutableStateOf("") }
    var fccPort by rememberSaveable { mutableStateOf("") }
    var fccJplPort by rememberSaveable { mutableStateOf("") }
    var fccAccessCode by remember { mutableStateOf("") } // NOT rememberSaveable — security
    var wsPort by rememberSaveable { mutableStateOf("") }
    var accessCodeHint by remember { mutableStateOf("Access code") }

    // Override indicators
    var fccIpOverridden by remember { mutableStateOf(false) }
    var fccPortOverridden by remember { mutableStateOf(false) }
    var fccJplPortOverridden by remember { mutableStateOf(false) }
    var fccAccessCodeOverridden by remember { mutableStateOf(false) }
    var wsPortOverridden by remember { mutableStateOf(false) }

    // Read-only fields
    var cloudBaseUrl by remember { mutableStateOf("Loading...") }
    var environment by remember { mutableStateOf("") }
    var deviceId by remember { mutableStateOf("") }
    var siteCode by remember { mutableStateOf("") }
    var routeBaseUrl by remember { mutableStateOf("") }

    // Status
    var statusText by remember { mutableStateOf("") }
    var statusColor by remember { mutableStateOf(StatusGreen) }
    var showStatus by remember { mutableStateOf(false) }

    // Dialogs
    var showResetDialog by remember { mutableStateOf(false) }
    var showReprovisionDialog by remember { mutableStateOf(false) }

    // Debounce
    var lastSaveClickTime by remember { mutableLongStateOf(0L) }

    // AT-008: Load field data off main thread
    var fieldsLoaded by remember { mutableStateOf(false) }
    LaunchedEffect(Unit) {
        withContext(Dispatchers.IO) {
            val config = configManager.config.value
            val cloudHost = config?.fcc?.hostAddress ?: encryptedPrefs.fccHost ?: ""
            val cloudPort = config?.fcc?.port
            val cloudCredential = config?.let {
                when {
                    !it.fcc.secretEnvelope.payload.isNullOrBlank() -> it.fcc.secretEnvelope.payload
                    !it.fcc.credentialRef.isNullOrBlank() -> it.fcc.credentialRef
                    else -> ""
                }
            } ?: ""

            fccIp = localOverrideManager.fccHost ?: cloudHost
            fccPort = (localOverrideManager.fccPort ?: cloudPort)?.toString() ?: ""
            fccJplPort = localOverrideManager.jplPort?.toString() ?: ""
            fccAccessCode = localOverrideManager.fccCredential ?: ""
            accessCodeHint = if (cloudCredential.isNotEmpty()) "Cloud credential set (hidden)" else "Access code"
            wsPort = localOverrideManager.wsPort?.toString() ?: ""

            fccIpOverridden = localOverrideManager.fccHost != null
            fccPortOverridden = localOverrideManager.fccPort != null
            fccJplPortOverridden = localOverrideManager.jplPort != null
            fccAccessCodeOverridden = localOverrideManager.fccCredential != null
            wsPortOverridden = localOverrideManager.wsPort != null

            val baseUrl = config?.sync?.cloudBaseUrl ?: encryptedPrefs.cloudBaseUrl
            cloudBaseUrl = baseUrl ?: "Not configured"
            environment = deriveEnvironment(baseUrl)
            deviceId = encryptedPrefs.deviceId ?: "Not provisioned"
            siteCode = config?.identity?.siteCode ?: encryptedPrefs.siteCode ?: "Not provisioned"
            routeBaseUrl = (baseUrl ?: "").trimEnd('/')

            fieldsLoaded = true
        }
    }

    fun reloadFieldData() {
        scope.launch(Dispatchers.IO) {
            val config = configManager.config.value
            val cloudHost = config?.fcc?.hostAddress ?: encryptedPrefs.fccHost ?: ""
            val cloudPort = config?.fcc?.port

            fccIp = localOverrideManager.fccHost ?: cloudHost
            fccPort = (localOverrideManager.fccPort ?: cloudPort)?.toString() ?: ""
            fccJplPort = localOverrideManager.jplPort?.toString() ?: ""
            fccAccessCode = localOverrideManager.fccCredential ?: ""
            wsPort = localOverrideManager.wsPort?.toString() ?: ""

            fccIpOverridden = localOverrideManager.fccHost != null
            fccPortOverridden = localOverrideManager.fccPort != null
            fccJplPortOverridden = localOverrideManager.jplPort != null
            fccAccessCodeOverridden = localOverrideManager.fccCredential != null
            wsPortOverridden = localOverrideManager.wsPort != null

            val baseUrl = config?.sync?.cloudBaseUrl ?: encryptedPrefs.cloudBaseUrl
            routeBaseUrl = (baseUrl ?: "").trimEnd('/')
        }
    }

    Column(
        modifier = Modifier
            .fillMaxSize()
            .background(Color.White)
            .verticalScroll(rememberScrollState())
            .padding(16.dp),
    ) {
        // Editable section
        SectionHeader("FCC Connection Overrides")

        OverrideTextField(
            label = "FCC IP / Hostname",
            value = fccIp,
            onValueChange = { fccIp = it },
            placeholder = "e.g., 192.168.1.100",
            isOverridden = fccIpOverridden,
        )
        OverrideTextField(
            label = "FCC Port",
            value = fccPort,
            onValueChange = { fccPort = it },
            placeholder = "e.g., 10001",
            keyboardType = KeyboardType.Number,
            isOverridden = fccPortOverridden,
        )
        OverrideTextField(
            label = "FCC JPL Port",
            value = fccJplPort,
            onValueChange = { fccJplPort = it },
            placeholder = "e.g., 10002",
            keyboardType = KeyboardType.Number,
            isOverridden = fccJplPortOverridden,
        )
        OverrideTextField(
            label = "FCC Access Code",
            value = fccAccessCode,
            onValueChange = { fccAccessCode = it },
            placeholder = accessCodeHint,
            isPassword = true,
            isOverridden = fccAccessCodeOverridden,
        )
        OverrideTextField(
            label = "WebSocket Port",
            value = wsPort,
            onValueChange = { wsPort = it },
            placeholder = "e.g., 8080",
            keyboardType = KeyboardType.Number,
            isOverridden = wsPortOverridden,
        )

        // Read-only section
        SectionHeader("Device Information (Read-Only)")
        DataRow("Cloud Base URL:", cloudBaseUrl)
        DataRow("Environment:", environment)
        DataRow("Device ID:", deviceId)
        DataRow("Site Code:", siteCode)

        // Cloud API Routes
        SectionHeader("Cloud API Routes (Read-Only)")
        CLOUD_API_ROUTES.forEach { (name, path) ->
            MonoDataRow(name, if (routeBaseUrl.isNotEmpty()) "$routeBaseUrl$path" else "Not configured")
        }

        // Status text
        if (showStatus) {
            Spacer(modifier = Modifier.height(16.dp))
            Text(text = statusText, fontSize = 13.sp, color = statusColor)
        }

        Spacer(modifier = Modifier.height(16.dp))

        // Buttons
        PumaButton(
            text = "Save & Reconnect",
            onClick = {
                val now = SystemClock.elapsedRealtime()
                if (now - lastSaveClickTime < DEBOUNCE_MS) return@PumaButton
                lastSaveClickTime = now

                val errors = validateSettings(fccIp, fccPort, fccJplPort, wsPort)
                if (errors.isNotEmpty()) {
                    statusText = errors.joinToString("\n")
                    statusColor = StatusRed
                    showStatus = true
                    return@PumaButton
                }

                scope.launch(Dispatchers.IO) {
                    try {
                        saveOverride(localOverrideManager, LocalOverrideManager.KEY_FCC_HOST, fccIp)
                        saveOverride(localOverrideManager, LocalOverrideManager.KEY_FCC_PORT, fccPort)
                        saveOverride(localOverrideManager, LocalOverrideManager.KEY_FCC_JPL_PORT, fccJplPort)
                        saveOverride(localOverrideManager, LocalOverrideManager.KEY_FCC_CREDENTIAL, fccAccessCode)
                        saveOverride(localOverrideManager, LocalOverrideManager.KEY_WS_PORT, wsPort)

                        AppLogger.i(TAG, "Overrides saved, requesting FCC reconnect")
                        cadenceController.requestFccReconnect()

                        withContext(Dispatchers.Main) {
                            statusText = "Settings saved. FCC reconnecting..."
                            statusColor = StatusGreen
                            showStatus = true
                            Toast.makeText(context, "Settings saved & reconnecting", Toast.LENGTH_SHORT).show()
                        }
                        reloadFieldData()
                    } catch (e: Exception) {
                        AppLogger.e(TAG, "Failed to save overrides", e)
                        withContext(Dispatchers.Main) {
                            statusText = "Error: ${e.message}"
                            statusColor = StatusRed
                            showStatus = true
                        }
                    }
                }
            },
            modifier = Modifier.fillMaxWidth(),
        )

        Spacer(modifier = Modifier.height(8.dp))

        PumaOutlinedButton(
            text = "Reset to Cloud Defaults",
            onClick = { showResetDialog = true },
            modifier = Modifier.fillMaxWidth(),
            borderColor = PumaRed,
        )

        Spacer(modifier = Modifier.height(16.dp))

        PumaOutlinedButton(
            text = "Reprovision Device",
            onClick = { showReprovisionDialog = true },
            modifier = Modifier.fillMaxWidth(),
            borderColor = PumaRed,
        )

        Spacer(modifier = Modifier.height(8.dp))

        PumaOutlinedButton(
            text = "Back",
            onClick = { navController.popBackStack() },
            modifier = Modifier.fillMaxWidth(),
        )

        Spacer(modifier = Modifier.height(16.dp))
    }

    // Reset dialog
    if (showResetDialog) {
        AlertDialog(
            onDismissRequest = { showResetDialog = false },
            title = { Text("Reset to Cloud Defaults") },
            text = { Text("Clear all local overrides and revert to cloud-delivered configuration?") },
            confirmButton = {
                TextButton(onClick = {
                    showResetDialog = false
                    scope.launch(Dispatchers.IO) {
                        localOverrideManager.clearAllOverrides()
                        AppLogger.i(TAG, "All overrides cleared, requesting FCC reconnect")
                        cadenceController.requestFccReconnect()
                        reloadFieldData()
                        withContext(Dispatchers.Main) {
                            statusText = "Overrides cleared. Using cloud defaults."
                            statusColor = StatusGreen
                            showStatus = true
                            Toast.makeText(context, "Reset to cloud defaults", Toast.LENGTH_SHORT).show()
                        }
                    }
                }) { Text("Reset") }
            },
            dismissButton = {
                TextButton(onClick = { showResetDialog = false }) { Text("Cancel") }
            },
        )
    }

    // Reprovision dialog
    if (showReprovisionDialog) {
        AlertDialog(
            onDismissRequest = { showReprovisionDialog = false },
            title = { Text("Reprovision Device") },
            text = {
                Text(
                    "This will clear all registration data, tokens, buffered transactions, " +
                        "and local overrides. The device will return to the provisioning screen " +
                        "and require a new bootstrap token.\n\n" +
                        "Use this when moving a device to a different site.\n\n" +
                        "Are you sure?",
                )
            },
            confirmButton = {
                TextButton(onClick = {
                    showReprovisionDialog = false
                    scope.launch {
                        try {
                            ReprovisionHelper.execute(
                                context = context,
                                bufferDatabase = bufferDatabase,
                                keystoreManager = keystoreManager,
                                encryptedPrefs = encryptedPrefs,
                                localOverrideManager = localOverrideManager,
                            )
                            navController.navigate("${Routes.PROVISIONING}?reason=manual_reprovision") {
                                popUpTo(0) { inclusive = true }
                            }
                        } catch (e: Exception) {
                            AppLogger.e(TAG, "Failed to reprovision", e)
                            statusText = "Reprovision failed: ${e.message}"
                            statusColor = StatusRed
                            showStatus = true
                        }
                    }
                }) { Text("Reprovision") }
            },
            dismissButton = {
                TextButton(onClick = { showReprovisionDialog = false }) { Text("Cancel") }
            },
        )
    }
}

@Composable
private fun OverrideTextField(
    label: String,
    value: String,
    onValueChange: (String) -> Unit,
    placeholder: String,
    keyboardType: KeyboardType = KeyboardType.Text,
    isPassword: Boolean = false,
    isOverridden: Boolean = false,
) {
    Column(modifier = Modifier.padding(vertical = 4.dp)) {
        Row {
            Text(text = label, fontSize = 14.sp, color = TextLabel)
            if (isOverridden) {
                Text(
                    text = "  (overridden)",
                    fontSize = 12.sp,
                    color = TextOverride,
                )
            }
        }
        OutlinedTextField(
            value = value,
            onValueChange = onValueChange,
            placeholder = { Text(placeholder, color = TextGray) },
            singleLine = true,
            keyboardOptions = KeyboardOptions(keyboardType = keyboardType),
            visualTransformation = if (isPassword) PasswordVisualTransformation() else androidx.compose.ui.text.input.VisualTransformation.None,
            modifier = Modifier.fillMaxWidth(),
        )
    }
}

private fun saveOverride(manager: LocalOverrideManager, key: String, value: String) {
    if (value.isNotEmpty()) {
        manager.saveOverride(key, value)
    } else {
        manager.clearOverride(key)
    }
}

private fun validateSettings(
    fccIp: String,
    fccPort: String,
    fccJplPort: String,
    wsPort: String,
): List<String> {
    val errors = mutableListOf<String>()

    if (fccIp.isNotEmpty() && !LocalOverrideManager.isValidHostOrIp(fccIp)) {
        errors += "FCC IP: must be a valid IPv4 address or hostname"
    }
    if (fccPort.isNotEmpty()) {
        val port = fccPort.toIntOrNull()
        if (port == null || !LocalOverrideManager.isValidPort(port)) {
            errors += "FCC Port: must be a number between 1 and 65535"
        }
    }
    if (fccJplPort.isNotEmpty()) {
        val port = fccJplPort.toIntOrNull()
        if (port == null || !LocalOverrideManager.isValidPort(port)) {
            errors += "FCC JPL Port: must be a number between 1 and 65535"
        }
    }
    if (wsPort.isNotEmpty()) {
        val port = wsPort.toIntOrNull()
        if (port == null || !LocalOverrideManager.isValidPort(port)) {
            errors += "WebSocket Port: must be a number between 1 and 65535"
        }
    }

    // AF-007: Cross-field validation — reject duplicate port assignments
    val portEntries = mutableListOf<Pair<String, Int>>()
    fccPort.toIntOrNull()?.let { portEntries += "FCC Port" to it }
    fccJplPort.toIntOrNull()?.let { portEntries += "FCC JPL Port" to it }
    wsPort.toIntOrNull()?.let { portEntries += "WebSocket Port" to it }
    val seen = mutableMapOf<Int, String>()
    for ((name, port) in portEntries) {
        val existing = seen[port]
        if (existing != null) {
            errors += "$name conflicts with $existing (both use port $port)"
        } else {
            seen[port] = name
        }
    }

    return errors
}

private fun deriveEnvironment(cloudUrl: String?): String {
    if (cloudUrl == null) return "Unknown"
    return when {
        cloudUrl.contains("staging", ignoreCase = true) -> "Staging"
        cloudUrl.contains("dev", ignoreCase = true) -> "Development"
        cloudUrl.contains("uat", ignoreCase = true) -> "UAT"
        else -> "Production"
    }
}
