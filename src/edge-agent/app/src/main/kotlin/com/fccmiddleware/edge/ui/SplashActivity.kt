package com.fccmiddleware.edge.ui

import android.app.Activity
import android.content.Intent
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.view.Gravity
import android.widget.FrameLayout
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.TextView
import androidx.appcompat.app.AppCompatActivity
import com.fccmiddleware.edge.R

class SplashActivity : AppCompatActivity() {

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(buildLayout())

        Handler(Looper.getMainLooper()).postDelayed({
            startActivity(Intent(this, LauncherActivity::class.java))
            // L-01: Transition animation must be set before finish() to affect the outgoing transition
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.UPSIDE_DOWN_CAKE) {
                overrideActivityTransition(Activity.OVERRIDE_TRANSITION_CLOSE, android.R.anim.fade_in, android.R.anim.fade_out)
            } else {
                @Suppress("DEPRECATION")
                overridePendingTransition(android.R.anim.fade_in, android.R.anim.fade_out)
            }
            finish()
        }, 2000)
    }

    private fun buildLayout(): FrameLayout {
        val density = resources.displayMetrics.density

        val root = FrameLayout(this).apply {
            setBackgroundColor(0xFFFFFFFF.toInt())
        }

        val centerLayout = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            gravity = Gravity.CENTER
        }

        val logo = ImageView(this).apply {
            setImageResource(R.drawable.splash_logo)
            adjustViewBounds = true
            val size = (180 * density).toInt()
            layoutParams = LinearLayout.LayoutParams(size, size).apply {
                gravity = Gravity.CENTER_HORIZONTAL
            }
        }
        centerLayout.addView(logo)

        val appName = TextView(this).apply {
            text = "Puma Energy"
            textSize = 24f
            setTextColor(0xFFE30613.toInt())
            gravity = Gravity.CENTER
            setPadding(0, (16 * density).toInt(), 0, 0)
        }
        centerLayout.addView(appName)

        val subtitle = TextView(this).apply {
            text = "FCC Edge Agent"
            textSize = 14f
            setTextColor(0xFF666666.toInt())
            gravity = Gravity.CENTER
            setPadding(0, (4 * density).toInt(), 0, 0)
        }
        centerLayout.addView(subtitle)

        root.addView(centerLayout, FrameLayout.LayoutParams(
            FrameLayout.LayoutParams.MATCH_PARENT,
            FrameLayout.LayoutParams.MATCH_PARENT,
            Gravity.CENTER
        ))

        return root
    }
}
