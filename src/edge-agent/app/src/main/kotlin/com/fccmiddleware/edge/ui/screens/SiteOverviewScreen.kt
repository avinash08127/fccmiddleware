package com.fccmiddleware.edge.ui.screens

import android.widget.Toast
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.navigation.NavHostController
import com.fccmiddleware.edge.adapter.common.ConnectivityState
import com.fccmiddleware.edge.adapter.common.PumpState
import com.fccmiddleware.edge.service.EdgeAgentForegroundService
import com.fccmiddleware.edge.ui.SiteOverviewViewModel
import com.fccmiddleware.edge.ui.navigation.Routes
import com.fccmiddleware.edge.ui.theme.PumaButton
import com.fccmiddleware.edge.ui.theme.PumaGreen
import com.fccmiddleware.edge.ui.theme.PumaOutlinedButton
import com.fccmiddleware.edge.ui.theme.StatusBlue
import com.fccmiddleware.edge.ui.theme.StatusDispensing
import com.fccmiddleware.edge.ui.theme.StatusGreen
import com.fccmiddleware.edge.ui.theme.StatusRed
import com.fccmiddleware.edge.ui.theme.StatusYellow
import com.fccmiddleware.edge.ui.theme.TextGray
import com.fccmiddleware.edge.ui.theme.TextLabel
import com.fccmiddleware.edge.ui.theme.TextPrimary
import org.koin.androidx.compose.koinViewModel

@Composable
fun SiteOverviewScreen(navController: NavHostController) {
    val viewModel: SiteOverviewViewModel = koinViewModel()
    val snapshot by viewModel.snapshot.collectAsStateWithLifecycle()
    val context = LocalContext.current

    DisposableEffect(Unit) {
        viewModel.startAutoRefresh()
        onDispose { viewModel.stopAutoRefresh() }
    }

    Column(modifier = Modifier.fillMaxSize()) {
        // Scrollable content
        LazyColumn(
            modifier = Modifier
                .weight(1f)
                .padding(horizontal = 16.dp, vertical = 8.dp),
        ) {
            item {
                val data = snapshot
                // Site header
                Text(
                    text = data?.siteName ?: "Loading...",
                    fontSize = 18.sp,
                    fontWeight = FontWeight.Bold,
                    color = TextPrimary,
                )
                Text(
                    text = buildString {
                        data?.siteCode?.let { append("Site: $it") }
                        data?.fccVendor?.let {
                            if (isNotEmpty()) append("  \u2022  ")
                            append("FCC: $it")
                        }
                    },
                    fontSize = 13.sp,
                    color = TextLabel,
                    modifier = Modifier.padding(top = 2.dp, bottom = 4.dp),
                )

                // Connectivity row
                val connState = data?.connectivityState ?: ConnectivityState.FULLY_OFFLINE
                val connColor = when (connState) {
                    ConnectivityState.FULLY_ONLINE -> StatusGreen
                    ConnectivityState.INTERNET_DOWN -> StatusYellow
                    else -> StatusRed
                }
                Row(
                    verticalAlignment = Alignment.CenterVertically,
                    modifier = Modifier.padding(bottom = 12.dp),
                ) {
                    Box(
                        modifier = Modifier
                            .size(10.dp)
                            .clip(CircleShape)
                            .background(connColor),
                    )
                    Spacer(modifier = Modifier.width(6.dp))
                    Text(
                        text = connState.name.replace('_', ' '),
                        fontSize = 13.sp,
                        fontWeight = FontWeight.Bold,
                        color = connColor,
                    )
                }
            }

            val data = snapshot
            if (data != null && data.pumpCards.isEmpty()) {
                item {
                    Text(
                        text = "Awaiting Configuration...",
                        fontSize = 16.sp,
                        color = TextGray,
                        textAlign = TextAlign.Center,
                        modifier = Modifier
                            .fillMaxWidth()
                            .padding(vertical = 40.dp),
                    )
                }
            } else if (data != null) {
                items(data.pumpCards, key = { it.odooPumpNumber }) { card ->
                    PumpCard(card)
                    Spacer(modifier = Modifier.height(10.dp))
                }
            }

            item {
                Spacer(modifier = Modifier.height(12.dp))
                Text(
                    text = "Last refresh: ${snapshot?.lastRefreshedAt ?: ""}",
                    fontSize = 11.sp,
                    color = TextGray,
                    textAlign = TextAlign.Center,
                    modifier = Modifier.fillMaxWidth(),
                )
            }
        }

        // Fixed bottom bar
        HorizontalDivider(color = Color(0xFFE0E0E0))
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .background(Color.White)
                .padding(horizontal = 16.dp, vertical = 8.dp),
            horizontalArrangement = Arrangement.spacedBy(8.dp),
        ) {
            PumaButton(
                text = "Diagnostics",
                onClick = {
                    navController.navigate(Routes.DIAGNOSTICS) {
                        popUpTo(Routes.SITE_OVERVIEW) { inclusive = false }
                        launchSingleTop = true
                    }
                },
                modifier = Modifier.weight(1f),
            )
            PumaButton(
                text = "Settings",
                onClick = {
                    navController.navigate(Routes.SETTINGS) {
                        popUpTo(Routes.SITE_OVERVIEW) { inclusive = false }
                        launchSingleTop = true
                    }
                },
                modifier = Modifier.weight(1f),
            )
            PumaOutlinedButton(
                text = "Refresh",
                onClick = {
                    EdgeAgentForegroundService.requestImmediateConfigPoll(
                        context,
                        "site_overview_manual_refresh",
                    )
                    Toast.makeText(context, "Config refresh requested", Toast.LENGTH_SHORT).show()
                },
                modifier = Modifier.weight(1f),
            )
        }
    }
}

@Composable
private fun PumpCard(data: SiteOverviewViewModel.PumpCardData) {
    val stateColor = pumpStateColor(data.state)

    Column(
        modifier = Modifier
            .fillMaxWidth()
            .border(2.dp, stateColor, RoundedCornerShape(8.dp))
            .padding(12.dp),
    ) {
        // Header row: pump name + state
        Row(verticalAlignment = Alignment.CenterVertically) {
            val pumpLabel = if (data.displayName.isNotEmpty()) {
                "${data.displayName} (Pump ${data.odooPumpNumber})"
            } else {
                "Pump ${data.odooPumpNumber}"
            }
            val headerText = if (data.odooPumpNumber != data.fccPumpNumber) {
                "$pumpLabel  \u2022  FCC #${data.fccPumpNumber}"
            } else {
                pumpLabel
            }
            Text(
                text = headerText,
                fontSize = 16.sp,
                fontWeight = FontWeight.Bold,
                color = TextPrimary,
                modifier = Modifier.weight(1f),
            )
            Box(
                modifier = Modifier
                    .size(8.dp)
                    .clip(CircleShape)
                    .background(stateColor),
            )
            Spacer(modifier = Modifier.width(4.dp))
            Text(
                text = data.state?.name ?: "NO STATUS",
                fontSize = 12.sp,
                fontWeight = FontWeight.Bold,
                color = stateColor,
            )
        }

        // Dispensing row
        if (data.state == PumpState.DISPENSING && data.currentVolumeLitres != null) {
            Spacer(modifier = Modifier.height(6.dp))
            Row {
                Text(
                    text = "Vol: ${data.currentVolumeLitres} L",
                    fontSize = 13.sp,
                    fontWeight = FontWeight.Bold,
                    color = StatusDispensing,
                    modifier = Modifier.weight(1f),
                )
                Text(
                    text = "Amt: ${data.currentAmount ?: "-"}",
                    fontSize = 13.sp,
                    fontWeight = FontWeight.Bold,
                    color = StatusDispensing,
                    modifier = Modifier.weight(1f),
                )
                Text(
                    text = "Price: ${data.unitPrice ?: "-"}/${data.currencyCode ?: ""}",
                    fontSize = 12.sp,
                    color = TextLabel,
                    textAlign = TextAlign.End,
                    modifier = Modifier.weight(1f),
                )
            }
        }

        // Divider + nozzles
        if (data.nozzles.isNotEmpty()) {
            Spacer(modifier = Modifier.height(6.dp))
            HorizontalDivider(color = Color(0xFFE0E0E0))
            Spacer(modifier = Modifier.height(4.dp))

            data.nozzles.forEach { nozzle ->
                Row(
                    verticalAlignment = Alignment.CenterVertically,
                    modifier = Modifier
                        .fillMaxWidth()
                        .then(
                            if (nozzle.isActiveOnPump) Modifier.background(Color(0x0DFF9800))
                            else Modifier
                        )
                        .padding(horizontal = 4.dp, vertical = 3.dp),
                ) {
                    Text(
                        text = "N${nozzle.fccNozzleNumber}",
                        fontSize = 13.sp,
                        fontFamily = FontFamily.Monospace,
                        fontWeight = FontWeight.Bold,
                        color = PumaGreen,
                    )
                    Spacer(modifier = Modifier.width(8.dp))
                    Text(
                        text = nozzle.productDisplayName,
                        fontSize = 13.sp,
                        color = TextPrimary,
                        modifier = Modifier.weight(1f),
                    )
                    if (nozzle.isActiveOnPump) {
                        Text(
                            text = "ACTIVE",
                            fontSize = 10.sp,
                            fontWeight = FontWeight.Bold,
                            color = Color.White,
                            modifier = Modifier
                                .background(StatusDispensing, RoundedCornerShape(4.dp))
                                .padding(horizontal = 6.dp, vertical = 1.dp),
                        )
                    }
                }
            }
        }
    }
}

private fun pumpStateColor(state: PumpState?): Color = when (state) {
    PumpState.IDLE, PumpState.COMPLETED -> StatusGreen
    PumpState.AUTHORIZED, PumpState.CALLING -> StatusBlue
    PumpState.DISPENSING -> StatusDispensing
    PumpState.PAUSED -> StatusYellow
    PumpState.ERROR -> StatusRed
    PumpState.OFFLINE, PumpState.UNKNOWN -> TextGray
    null -> Color(0xFFBDBDBD)
}
