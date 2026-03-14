package com.fccmiddleware.edge.ui.screens

import androidx.activity.compose.BackHandler
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.navigation.NavHostController
import com.fccmiddleware.edge.buffer.BufferDatabase
import com.fccmiddleware.edge.config.LocalOverrideManager
import com.fccmiddleware.edge.security.EncryptedPrefsManager
import com.fccmiddleware.edge.security.KeystoreManager
import com.fccmiddleware.edge.ui.ReprovisionHelper
import com.fccmiddleware.edge.ui.navigation.Routes
import com.fccmiddleware.edge.ui.theme.PumaButton
import com.fccmiddleware.edge.ui.theme.PumaGreen
import com.fccmiddleware.edge.ui.theme.PumaLogo
import com.fccmiddleware.edge.ui.theme.PumaRed
import com.fccmiddleware.edge.ui.theme.TextLabel
import kotlinx.coroutines.launch
import org.koin.compose.koinInject

@Composable
fun DecommissionedScreen(navController: NavHostController) {
    val context = LocalContext.current
    val bufferDatabase: BufferDatabase = koinInject()
    val keystoreManager: KeystoreManager = koinInject()
    val encryptedPrefs: EncryptedPrefsManager = koinInject()
    val localOverrideManager: LocalOverrideManager = koinInject()
    val scope = rememberCoroutineScope()
    var showConfirmDialog by remember { mutableStateOf(false) }

    // Prevent navigating back — device is decommissioned
    BackHandler { /* no-op */ }

    Column(
        modifier = Modifier
            .fillMaxSize()
            .background(Color.White)
            .verticalScroll(rememberScrollState())
            .padding(24.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        Spacer(modifier = Modifier.height(32.dp))

        PumaLogo(modifier = Modifier.width(160.dp))

        Spacer(modifier = Modifier.height(12.dp))

        // Warning icon
        Text(
            text = "X",
            fontSize = 64.sp,
            fontWeight = FontWeight.Bold,
            color = PumaRed,
        )

        Spacer(modifier = Modifier.height(12.dp))

        Text(
            text = "Device Decommissioned",
            fontSize = 24.sp,
            fontWeight = FontWeight.Bold,
            color = PumaRed,
            textAlign = TextAlign.Center,
        )

        Spacer(modifier = Modifier.height(12.dp))

        // Red accent divider
        Spacer(
            modifier = Modifier
                .width(60.dp)
                .height(3.dp)
                .background(PumaRed),
        )

        Spacer(modifier = Modifier.height(24.dp))

        Text(
            text = "This device has been decommissioned by the management portal.\n\n" +
                "All synchronization has been stopped and credentials have been revoked.\n\n" +
                "To re-activate this device:\n" +
                "1. Contact your site supervisor or IT administrator\n" +
                "2. Request a new provisioning QR code from the admin portal\n" +
                "3. Tap \"Re-Provision Device\" below and scan the new QR code",
            fontSize = 16.sp,
            color = TextLabel,
        )

        Spacer(modifier = Modifier.height(24.dp))

        PumaButton(
            text = "Re-Provision Device",
            onClick = { showConfirmDialog = true },
            modifier = Modifier.fillMaxWidth(),
            color = PumaGreen,
        )
    }

    if (showConfirmDialog) {
        AlertDialog(
            onDismissRequest = { showConfirmDialog = false },
            title = { Text("Re-Provision Device?") },
            text = {
                Text(
                    "This will clear all local data and credentials. " +
                        "You will need a new provisioning QR code from the admin portal to continue.",
                )
            },
            confirmButton = {
                TextButton(onClick = {
                    showConfirmDialog = false
                    scope.launch {
                        ReprovisionHelper.execute(
                            context = context,
                            bufferDatabase = bufferDatabase,
                            keystoreManager = keystoreManager,
                            encryptedPrefs = encryptedPrefs,
                            localOverrideManager = localOverrideManager,
                        )
                        navController.navigate(Routes.PROVISIONING) {
                            popUpTo(0) { inclusive = true }
                        }
                    }
                }) { Text("Re-Provision") }
            },
            dismissButton = {
                TextButton(onClick = { showConfirmDialog = false }) { Text("Cancel") }
            },
        )
    }
}
