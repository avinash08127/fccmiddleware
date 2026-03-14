package com.fccmiddleware.edge.ui.navigation

import androidx.compose.runtime.Composable
import androidx.navigation.NavHostController
import androidx.navigation.compose.NavHost
import androidx.navigation.compose.composable
import androidx.navigation.navArgument
import androidx.navigation.NavType
import com.fccmiddleware.edge.ui.screens.DecommissionedScreen
import com.fccmiddleware.edge.ui.screens.DiagnosticsScreen
import com.fccmiddleware.edge.ui.screens.LauncherScreen
import com.fccmiddleware.edge.ui.screens.ProvisioningScreen
import com.fccmiddleware.edge.ui.screens.SettingsScreen
import com.fccmiddleware.edge.ui.screens.SiteOverviewScreen
import com.fccmiddleware.edge.ui.screens.SplashScreen

object Routes {
    const val SPLASH = "splash"
    const val LAUNCHER = "launcher"
    const val PROVISIONING = "provisioning"
    const val SITE_OVERVIEW = "siteOverview"
    const val DIAGNOSTICS = "diagnostics"
    const val SETTINGS = "settings"
    const val DECOMMISSIONED = "decommissioned"
}

@Composable
fun AppNavHost(navController: NavHostController) {
    NavHost(navController = navController, startDestination = Routes.SPLASH) {
        // Standalone screens — no drawer
        composable(Routes.SPLASH) {
            SplashScreen(navController)
        }
        composable(Routes.LAUNCHER) {
            LauncherScreen(navController)
        }
        composable(
            route = "${Routes.PROVISIONING}?reason={reason}",
            arguments = listOf(navArgument("reason") {
                type = NavType.StringType
                defaultValue = ""
            }),
        ) { backStackEntry ->
            ProvisioningScreen(
                navController = navController,
                reason = backStackEntry.arguments?.getString("reason") ?: "",
            )
        }
        composable(Routes.DECOMMISSIONED) {
            DecommissionedScreen(navController)
        }

        // Main screens — wrapped in shared drawer scaffold
        composable(Routes.SITE_OVERVIEW) {
            AppScaffold(navController = navController, currentRoute = Routes.SITE_OVERVIEW) {
                SiteOverviewScreen(navController)
            }
        }
        composable(Routes.DIAGNOSTICS) {
            AppScaffold(navController = navController, currentRoute = Routes.DIAGNOSTICS) {
                DiagnosticsScreen(navController)
            }
        }
        composable(Routes.SETTINGS) {
            AppScaffold(navController = navController, currentRoute = Routes.SETTINGS) {
                SettingsScreen(navController)
            }
        }
    }
}
