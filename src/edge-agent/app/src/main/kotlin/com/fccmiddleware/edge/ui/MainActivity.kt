package com.fccmiddleware.edge.ui

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.navigation.compose.rememberNavController
import com.fccmiddleware.edge.ui.navigation.AppNavHost
import com.fccmiddleware.edge.ui.theme.FccEdgeAgentTheme

class MainActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        enableEdgeToEdge()
        super.onCreate(savedInstanceState)
        setContent {
            FccEdgeAgentTheme {
                val navController = rememberNavController()
                AppNavHost(navController)
            }
        }
    }
}
