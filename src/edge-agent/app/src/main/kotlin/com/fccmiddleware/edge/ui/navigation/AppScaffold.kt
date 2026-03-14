package com.fccmiddleware.edge.ui.navigation

import androidx.compose.foundation.Image
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Menu
import androidx.compose.material3.DrawerValue
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.ModalDrawerSheet
import androidx.compose.material3.ModalNavigationDrawer
import androidx.compose.material3.NavigationDrawerItem
import androidx.compose.material3.NavigationDrawerItemDefaults
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBar
import androidx.compose.material3.TopAppBarDefaults
import androidx.compose.material3.rememberDrawerState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.navigation.NavHostController
import com.fccmiddleware.edge.R
import com.fccmiddleware.edge.ui.theme.PumaGreen
import com.fccmiddleware.edge.ui.theme.PumaRed
import com.fccmiddleware.edge.ui.theme.TextPrimary
import kotlinx.coroutines.launch

private fun titleForRoute(route: String): String = when (route) {
    Routes.SITE_OVERVIEW -> "Site Overview"
    Routes.DIAGNOSTICS -> "Edge Agent Diagnostics"
    Routes.SETTINGS -> "FCC Connection Settings"
    else -> ""
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun AppScaffold(
    navController: NavHostController,
    currentRoute: String,
    content: @Composable () -> Unit,
) {
    val drawerState = rememberDrawerState(DrawerValue.Closed)
    val scope = rememberCoroutineScope()

    fun navigateTo(route: String) {
        scope.launch { drawerState.close() }
        if (route != currentRoute) {
            navController.navigate(route) {
                popUpTo(Routes.SITE_OVERVIEW) { inclusive = false }
                launchSingleTop = true
            }
        }
    }

    ModalNavigationDrawer(
        drawerState = drawerState,
        drawerContent = {
            ModalDrawerSheet(
                drawerContainerColor = Color.White,
                modifier = Modifier.width(280.dp),
            ) {
                // Puma branding header
                Column(modifier = Modifier.padding(24.dp)) {
                    Image(
                        painter = painterResource(id = R.drawable.puma_energy_logo),
                        contentDescription = "Puma Energy Logo",
                        modifier = Modifier.width(120.dp),
                    )
                    Spacer(modifier = Modifier.height(8.dp))
                    Text(
                        text = "Edge Agent",
                        fontSize = 16.sp,
                        fontWeight = FontWeight.Bold,
                        color = PumaGreen,
                    )
                }
                HorizontalDivider(color = PumaGreen, thickness = 2.dp)

                // Navigation items
                NavigationDrawerItem(
                    label = { Text("Site Overview") },
                    selected = currentRoute == Routes.SITE_OVERVIEW,
                    onClick = { navigateTo(Routes.SITE_OVERVIEW) },
                    colors = NavigationDrawerItemDefaults.colors(
                        selectedContainerColor = Color(0x1A007A33),
                        selectedTextColor = PumaGreen,
                        unselectedTextColor = TextPrimary,
                    ),
                    modifier = Modifier.padding(horizontal = 8.dp),
                )
                NavigationDrawerItem(
                    label = { Text("Diagnostics") },
                    selected = currentRoute == Routes.DIAGNOSTICS,
                    onClick = { navigateTo(Routes.DIAGNOSTICS) },
                    colors = NavigationDrawerItemDefaults.colors(
                        selectedContainerColor = Color(0x1A007A33),
                        selectedTextColor = PumaGreen,
                        unselectedTextColor = TextPrimary,
                    ),
                    modifier = Modifier.padding(horizontal = 8.dp),
                )
                NavigationDrawerItem(
                    label = { Text("Settings") },
                    selected = currentRoute == Routes.SETTINGS,
                    onClick = { navigateTo(Routes.SETTINGS) },
                    colors = NavigationDrawerItemDefaults.colors(
                        selectedContainerColor = Color(0x1A007A33),
                        selectedTextColor = PumaGreen,
                        unselectedTextColor = TextPrimary,
                    ),
                    modifier = Modifier.padding(horizontal = 8.dp),
                )
            }
        },
    ) {
        Scaffold(
            topBar = {
                Column {
                    TopAppBar(
                        title = {
                            Text(
                                text = titleForRoute(currentRoute),
                                fontWeight = FontWeight.Bold,
                                color = TextPrimary,
                            )
                        },
                        navigationIcon = {
                            IconButton(onClick = { scope.launch { drawerState.open() } }) {
                                Icon(Icons.Default.Menu, contentDescription = "Menu", tint = PumaGreen)
                            }
                        },
                        colors = TopAppBarDefaults.topAppBarColors(containerColor = Color.White),
                    )
                    HorizontalDivider(color = PumaRed, thickness = 2.dp)
                }
            },
            containerColor = Color.White,
        ) { innerPadding ->
            Box(Modifier.padding(innerPadding)) {
                content()
            }
        }
    }
}
