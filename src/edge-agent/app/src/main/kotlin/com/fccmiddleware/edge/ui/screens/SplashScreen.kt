package com.fccmiddleware.edge.ui.screens

import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.size
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.navigation.NavHostController
import com.fccmiddleware.edge.R
import com.fccmiddleware.edge.ui.navigation.Routes
import com.fccmiddleware.edge.ui.theme.PumaRed
import com.fccmiddleware.edge.ui.theme.TextGray
import kotlinx.coroutines.delay

@Composable
fun SplashScreen(navController: NavHostController) {
    // AP-005: 500ms delay — enough branding visibility, fast service start
    LaunchedEffect(Unit) {
        delay(500)
        navController.navigate(Routes.LAUNCHER) {
            popUpTo(Routes.SPLASH) { inclusive = true }
        }
    }

    Box(
        modifier = Modifier
            .fillMaxSize()
            .background(Color.White),
        contentAlignment = Alignment.Center,
    ) {
        Column(horizontalAlignment = Alignment.CenterHorizontally) {
            Image(
                painter = painterResource(id = R.drawable.splash_logo),
                contentDescription = "Puma Energy Logo",
                modifier = Modifier.size(180.dp),
            )
            Spacer(modifier = Modifier.height(16.dp))
            Text(
                text = "Puma Energy",
                fontSize = 24.sp,
                color = PumaRed,
            )
            Spacer(modifier = Modifier.height(4.dp))
            Text(
                text = "FCC Edge Agent",
                fontSize = 14.sp,
                color = TextGray,
            )
        }
    }
}
