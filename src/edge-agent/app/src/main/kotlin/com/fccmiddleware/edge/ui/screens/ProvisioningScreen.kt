package com.fccmiddleware.edge.ui.screens

import android.Manifest
import android.content.Intent
import android.content.pm.PackageManager
import android.view.WindowManager
import androidx.activity.ComponentActivity
import androidx.activity.compose.BackHandler
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.ExposedDropdownMenuBox
import androidx.compose.material3.ExposedDropdownMenuDefaults
import androidx.compose.material3.MenuAnchorType
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardCapitalization
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.core.content.ContextCompat
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.navigation.NavHostController
import com.fccmiddleware.edge.config.CloudEnvironments
import com.fccmiddleware.edge.logging.AppLogger
import com.fccmiddleware.edge.security.EncryptedPrefsManager
import com.fccmiddleware.edge.service.EdgeAgentForegroundService
import com.fccmiddleware.edge.ui.ProvisioningViewModel
import com.fccmiddleware.edge.ui.QrBootstrapData
import com.fccmiddleware.edge.ui.navigation.Routes
import com.fccmiddleware.edge.ui.theme.PumaButton
import com.fccmiddleware.edge.ui.theme.PumaGreen
import com.fccmiddleware.edge.ui.theme.PumaLogo
import com.fccmiddleware.edge.ui.theme.PumaOutlinedButton
import com.fccmiddleware.edge.ui.theme.PumaRed
import com.fccmiddleware.edge.ui.theme.TextGray
import com.fccmiddleware.edge.ui.theme.TextLabel
import com.fccmiddleware.edge.ui.theme.TextPrimary
import com.journeyapps.barcodescanner.ScanContract
import com.journeyapps.barcodescanner.ScanOptions
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.contentOrNull
import kotlinx.serialization.json.int
import kotlinx.serialization.json.jsonPrimitive
import org.koin.androidx.compose.koinViewModel
import org.koin.compose.koinInject

private const val TAG = "ProvisioningScreen"

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun ProvisioningScreen(
    navController: NavHostController,
    reason: String = "",
) {
    val viewModel: ProvisioningViewModel = koinViewModel()
    val encryptedPrefs: EncryptedPrefsManager = koinInject()
    val registrationState by viewModel.registrationState.collectAsStateWithLifecycle()
    val context = LocalContext.current

    val json = remember {
        Json {
            ignoreUnknownKeys = true
            isLenient = true
        }
    }

    // FLAG_SECURE
    DisposableEffect(Unit) {
        val activity = context as ComponentActivity
        activity.window.setFlags(WindowManager.LayoutParams.FLAG_SECURE, WindowManager.LayoutParams.FLAG_SECURE)
        onDispose { activity.window.clearFlags(WindowManager.LayoutParams.FLAG_SECURE) }
    }

    // AF-011: If already registered, redirect immediately
    LaunchedEffect(Unit) {
        if (encryptedPrefs.isRegistered) {
            AppLogger.i(TAG, "Device already registered — redirecting to site overview")
            try {
                context.startForegroundService(Intent(context, EdgeAgentForegroundService::class.java))
            } catch (e: Exception) {
                AppLogger.e(TAG, "Failed to start foreground service on redirect", e)
            }
            navController.navigate(Routes.SITE_OVERVIEW) {
                popUpTo(0) { inclusive = true }
            }
        }
    }

    // Screen state
    var showManualEntry by rememberSaveable { mutableStateOf(false) }
    var selectedEnvIndex by rememberSaveable { mutableIntStateOf(0) }
    var cloudUrlInput by rememberSaveable { mutableStateOf("") }
    var siteCodeInput by rememberSaveable { mutableStateOf("") }
    // AF-001: Token is NOT saved across rotation/process death
    var tokenInput by remember { mutableStateOf("") }
    var errorMessage by remember { mutableStateOf("") }
    var envDropdownExpanded by remember { mutableStateOf(false) }

    // Initialize cloud URL based on selected environment
    LaunchedEffect(selectedEnvIndex) {
        val envKey = CloudEnvironments.keys[selectedEnvIndex]
        val url = CloudEnvironments.resolve(envKey)
        if (url != null) {
            cloudUrlInput = url
        }
    }

    // Show reason banner
    LaunchedEffect(reason) {
        if (reason == "token_expired") {
            errorMessage = "Your device's authentication has expired. Please scan a new provisioning QR code from the admin portal."
        }
    }

    val isRegistering = registrationState is ProvisioningViewModel.RegistrationState.InProgress

    // QR scanner launcher
    val scanLauncher = rememberLauncherForActivityResult(ScanContract()) { result ->
        val contents = result.contents
        if (contents == null) {
            errorMessage = "QR scan cancelled. You can try again or use manual entry."
            return@rememberLauncherForActivityResult
        }
        val qrData = parseQrPayload(json, contents)
        if (qrData == null) {
            errorMessage = "Invalid QR code format. Expected provisioning QR with v, sc, cu, pt fields."
            return@rememberLauncherForActivityResult
        }
        errorMessage = ""
        viewModel.register(qrData)
    }

    // Camera permission launcher
    val permissionLauncher = rememberLauncherForActivityResult(
        ActivityResultContracts.RequestPermission()
    ) { granted ->
        if (granted) {
            launchScanner(scanLauncher)
        } else {
            errorMessage = "Camera permission is required to scan provisioning QR codes.\nYou can use manual entry instead."
        }
    }

    // Handle back press
    BackHandler {
        if (showManualEntry && !isRegistering) {
            showManualEntry = false
            errorMessage = ""
        }
        // If registering, ignore back press
    }

    // Handle registration state changes
    LaunchedEffect(registrationState) {
        when (val state = registrationState) {
            is ProvisioningViewModel.RegistrationState.Error -> {
                errorMessage = state.message
            }
            is ProvisioningViewModel.RegistrationState.Success -> {
                try {
                    context.startForegroundService(Intent(context, EdgeAgentForegroundService::class.java))
                } catch (e: Exception) {
                    AppLogger.e(TAG, "Failed to start foreground service", e)
                }
                navController.navigate(Routes.SITE_OVERVIEW) {
                    popUpTo(0) { inclusive = true }
                }
                viewModel.onNavigationComplete()
            }
            else -> {}
        }
    }

    Column(
        modifier = Modifier
            .fillMaxSize()
            .background(Color.White)
            .verticalScroll(rememberScrollState())
            .padding(16.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        Spacer(modifier = Modifier.height(32.dp))

        PumaLogo(modifier = Modifier.width(180.dp))

        Spacer(modifier = Modifier.height(8.dp))

        Text(
            text = "Puma Energy FCC Agent",
            fontSize = 22.sp,
            fontWeight = FontWeight.Bold,
            color = TextPrimary,
            textAlign = TextAlign.Center,
        )

        Spacer(modifier = Modifier.height(8.dp))

        // Red accent divider
        Spacer(
            modifier = Modifier
                .width(60.dp)
                .height(3.dp)
                .background(PumaRed),
        )

        Spacer(modifier = Modifier.height(16.dp))

        if (!showManualEntry) {
            // Method selection panel
            Text(
                text = "Choose how to register this device with the cloud.",
                fontSize = 16.sp,
                color = TextLabel,
                textAlign = TextAlign.Center,
                modifier = Modifier.padding(bottom = 32.dp),
            )

            PumaButton(
                text = "Scan QR Code",
                onClick = {
                    errorMessage = ""
                    if (ContextCompat.checkSelfPermission(context, Manifest.permission.CAMERA) == PackageManager.PERMISSION_GRANTED) {
                        launchScanner(scanLauncher)
                    } else {
                        permissionLauncher.launch(Manifest.permission.CAMERA)
                    }
                },
                enabled = !isRegistering,
                modifier = Modifier.fillMaxWidth(),
            )

            Spacer(modifier = Modifier.height(8.dp))

            Text(
                text = "\u2014 or \u2014",
                fontSize = 14.sp,
                color = TextGray,
                textAlign = TextAlign.Center,
            )

            Spacer(modifier = Modifier.height(8.dp))

            PumaOutlinedButton(
                text = "Enter Manually",
                onClick = {
                    showManualEntry = true
                    errorMessage = ""
                },
                modifier = Modifier.fillMaxWidth(),
            )
        } else {
            // Manual entry panel
            Text(
                text = "Manual Provisioning",
                fontSize = 18.sp,
                fontWeight = FontWeight.Bold,
                color = TextPrimary,
                textAlign = TextAlign.Center,
            )

            Spacer(modifier = Modifier.height(8.dp))

            Text(
                text = "Enter the provisioning details from the admin portal.",
                fontSize = 14.sp,
                color = TextLabel,
                textAlign = TextAlign.Center,
                modifier = Modifier.padding(bottom = 16.dp),
            )

            // Environment dropdown
            Text(text = "Environment", fontSize = 14.sp, color = TextLabel)
            ExposedDropdownMenuBox(
                expanded = envDropdownExpanded,
                onExpandedChange = { envDropdownExpanded = it },
            ) {
                OutlinedTextField(
                    value = CloudEnvironments.displayNames[selectedEnvIndex],
                    onValueChange = {},
                    readOnly = true,
                    trailingIcon = { ExposedDropdownMenuDefaults.TrailingIcon(expanded = envDropdownExpanded) },
                    modifier = Modifier
                        .fillMaxWidth()
                        .menuAnchor(MenuAnchorType.PrimaryNotEditable),
                )
                ExposedDropdownMenu(
                    expanded = envDropdownExpanded,
                    onDismissRequest = { envDropdownExpanded = false },
                ) {
                    CloudEnvironments.displayNames.forEachIndexed { index, name ->
                        DropdownMenuItem(
                            text = { Text(name) },
                            onClick = {
                                selectedEnvIndex = index
                                envDropdownExpanded = false
                                val envKey = CloudEnvironments.keys[index]
                                val url = CloudEnvironments.resolve(envKey)
                                cloudUrlInput = url ?: ""
                            },
                        )
                    }
                }
            }

            Spacer(modifier = Modifier.height(8.dp))

            // Cloud URL
            Text(text = "Cloud URL", fontSize = 14.sp, color = TextLabel)
            val envKey = CloudEnvironments.keys[selectedEnvIndex]
            val isUrlEditable = CloudEnvironments.resolve(envKey) == null
            OutlinedTextField(
                value = cloudUrlInput,
                onValueChange = { if (isUrlEditable) cloudUrlInput = it },
                enabled = isUrlEditable,
                placeholder = { Text("https://api.fccmiddleware.io") },
                singleLine = true,
                keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Uri),
                modifier = Modifier.fillMaxWidth(),
            )

            Spacer(modifier = Modifier.height(8.dp))

            // Site Code
            Text(text = "Site Code", fontSize = 14.sp, color = TextLabel)
            OutlinedTextField(
                value = siteCodeInput,
                onValueChange = { siteCodeInput = it },
                placeholder = { Text("e.g., SITE-001") },
                singleLine = true,
                keyboardOptions = KeyboardOptions(capitalization = KeyboardCapitalization.Characters),
                modifier = Modifier.fillMaxWidth(),
            )

            Spacer(modifier = Modifier.height(8.dp))

            // Provisioning Token
            Text(text = "Provisioning Token", fontSize = 14.sp, color = TextLabel)
            OutlinedTextField(
                value = tokenInput,
                onValueChange = { tokenInput = it },
                placeholder = { Text("Paste the one-time token from the admin portal") },
                singleLine = true,
                visualTransformation = PasswordVisualTransformation(),
                modifier = Modifier.fillMaxWidth(),
            )

            Spacer(modifier = Modifier.height(16.dp))

            // Button row
            Row(modifier = Modifier.fillMaxWidth()) {
                PumaOutlinedButton(
                    text = "Back",
                    onClick = {
                        showManualEntry = false
                        errorMessage = ""
                    },
                    modifier = Modifier.weight(1f),
                )
                Spacer(modifier = Modifier.width(16.dp))
                PumaButton(
                    text = "Register",
                    onClick = {
                        val resolvedUrl = CloudEnvironments.resolve(CloudEnvironments.keys[selectedEnvIndex])
                        val cloudUrl = resolvedUrl ?: cloudUrlInput.trim()
                        val code = siteCodeInput.trim()
                        val token = tokenInput.trim()

                        errorMessage = when {
                            cloudUrl.isBlank() -> "Please enter the Cloud URL."
                            !cloudUrl.startsWith("https://") -> "Cloud URL must start with https://"
                            code.isBlank() -> "Please enter the Site Code."
                            token.isBlank() -> "Please enter the Provisioning Token."
                            else -> ""
                        }
                        if (errorMessage.isNotEmpty()) return@PumaButton

                        AppLogger.i(TAG, "Manual entry submitted, starting registration")
                        viewModel.register(
                            QrBootstrapData(
                                siteCode = code,
                                cloudBaseUrl = cloudUrl.trimEnd('/'),
                                provisioningToken = token,
                                environment = CloudEnvironments.keys[selectedEnvIndex],
                            ),
                        )
                    },
                    enabled = !isRegistering,
                    modifier = Modifier.weight(1f),
                )
            }
        }

        // Progress indicator
        if (isRegistering) {
            Spacer(modifier = Modifier.height(16.dp))
            CircularProgressIndicator(color = PumaGreen)
            Spacer(modifier = Modifier.height(8.dp))
            Text(
                text = (registrationState as? ProvisioningViewModel.RegistrationState.InProgress)?.message ?: "",
                fontSize = 14.sp,
                color = TextLabel,
                textAlign = TextAlign.Center,
            )
        }

        // Error message
        if (errorMessage.isNotEmpty() && !isRegistering) {
            Spacer(modifier = Modifier.height(16.dp))
            Text(
                text = errorMessage,
                fontSize = 14.sp,
                color = PumaRed,
                textAlign = TextAlign.Center,
            )
        }
    }
}

private fun launchScanner(scanLauncher: androidx.activity.result.ActivityResultLauncher<ScanOptions>) {
    val options = ScanOptions().apply {
        setDesiredBarcodeFormats(ScanOptions.QR_CODE)
        setPrompt("Scan the provisioning QR code")
        setBeepEnabled(false)
        setOrientationLocked(true)
    }
    scanLauncher.launch(options)
}

/**
 * Parse QR code JSON payload.
 * v1: { "v": 1, "sc": "SITE-CODE", "cu": "https://...", "pt": "token" }
 * v2: { "v": 2, "sc": "SITE-CODE", "cu": "https://...", "pt": "token", "env": "STAGING" }
 */
private fun parseQrPayload(json: Json, rawJson: String): QrBootstrapData? {
    return try {
        val obj = json.decodeFromString<JsonObject>(rawJson)
        val version = obj["v"]?.jsonPrimitive?.int
        val siteCode = obj["sc"]?.jsonPrimitive?.content
        val cloudUrl = obj["cu"]?.jsonPrimitive?.content
        val token = obj["pt"]?.jsonPrimitive?.content
        val env = obj["env"]?.jsonPrimitive?.contentOrNull

        if (version == null || version !in 1..2) return null
        if (siteCode.isNullOrBlank() || token.isNullOrBlank()) return null

        val resolvedUrl = if (version == 2 && !env.isNullOrBlank()) {
            CloudEnvironments.resolve(env) ?: cloudUrl
        } else {
            cloudUrl
        }

        if (resolvedUrl.isNullOrBlank()) return null
        if (!resolvedUrl.startsWith("https://")) {
            AppLogger.w(TAG, "Cloud URL must use HTTPS — rejecting insecure QR code")
            return null
        }

        QrBootstrapData(
            siteCode = siteCode,
            cloudBaseUrl = resolvedUrl.trimEnd('/'),
            provisioningToken = token,
            environment = env?.uppercase(),
        )
    } catch (e: Exception) {
        AppLogger.w(TAG, "Failed to parse QR payload")
        null
    }
}
