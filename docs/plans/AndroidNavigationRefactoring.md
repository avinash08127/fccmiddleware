# Android Navigation Refactoring Plan

## Jetpack Compose + Compose Navigation (Option C)

**Date**: 2026-03-14
**Status**: Draft — awaiting approval before implementation
**Scope**: `src/edge-agent/app/`
**Current Architecture**: 7 Activities, 3 ViewModels, programmatic View-based UI, per-Activity DrawerLayout
**Target Architecture**: 1 Activity, Compose NavHost, `ModalNavigationDrawer`, 7 Composable screens

---

## 1. Why Refactor?

### Current Pain Points

1. **Drawer duplication** — `NavigationDrawerHelper` must be wired into every Activity individually. Adding a new screen means remembering to wrap it. The hamburger menu already failed to appear on `SiteOverviewActivity` because it was a screen we didn't know about.

2. **System-bar insets applied per-Activity** — Each Activity must call `WindowCompat.setDecorFitsSystemWindows` and `applyBottomSystemBarInset`. When one Activity misses it, the bottom bar hides behind the system navigation bar.

3. **Inconsistent navigation** — Some Activities use `FLAG_ACTIVITY_NEW_TASK | FLAG_ACTIVITY_CLEAR_TASK` (Provisioning success, reprovision), others use plain `startActivity()`. Back-stack behavior is hard to reason about.

4. **Duplicated reprovision logic** — Both `SettingsActivity` and `DecommissionedActivity` contain identical 8-step credential-clearing and service-stopping code.

5. **Verbose imperative UI** — 640 imperative View calls (`addView`, `setPadding`, `setTextColor`, `GradientDrawable`, etc.) across 3,855 lines of UI code. Every UI element requires 5-10 lines of `.apply { }` configuration. Adding or changing a section means navigating deeply nested builder chains.

6. **Manual state-to-UI binding** — 15 `lateinit var` TextView/EditText references in DiagnosticsActivity alone, plus manual pool management (`mutableListOf<TextView>()`, grow/shrink loops) for dynamic lists. This is error-prone boilerplate that Compose eliminates entirely.

7. **No deep-link support** — Cannot navigate to a specific screen from a push notification or external link without writing manual Intent routing.

---

## 2. Why Option C (Compose) Over Option A (Fragments)

### The decisive factor: the codebase has zero XML layouts

Every screen builds its UI programmatically in Kotlin:
```kotlin
// Current: 8 lines to create one styled button
root.addView(Button(this).apply {
    text = "Save & Reconnect"
    textSize = 16f
    setOnClickListener { saveAndReconnect() }
    background = GradientDrawable().apply {
        setColor(PUMA_GREEN)
        cornerRadius = 8 * resources.displayMetrics.density
    }
    setTextColor(Color.WHITE)
    isAllCaps = true
    setPadding(dp(32), halfPad, dp(32), halfPad)
})
```
```kotlin
// Compose: 5 lines for the same button
Button(
    onClick = { saveAndReconnect() },
    colors = ButtonDefaults.buttonColors(containerColor = PumaGreen),
    shape = RoundedCornerShape(8.dp),
) { Text("SAVE & RECONNECT", color = Color.White) }
```

Option A (Fragments) would move this same verbose imperative code from Activities into Fragments — changing `this` to `requireContext()` on 640+ View construction calls. You do all that refactoring work and the UI code is still painful.

### Head-to-head comparison

| Dimension | Option A (Fragments) | Option C (Compose) |
|---|---|---|
| **Navigation** | NavGraph XML + Fragment transactions | Compose NavHost — Kotlin-only, type-safe |
| **Drawer** | DrawerLayout in Activity XML/code | `ModalNavigationDrawer` — 20 lines, built-in |
| **Insets** | Manual `fitsSystemWindows` / `ViewCompat` | `Scaffold` handles padding automatically |
| **UI code change** | ~200 `this` → `requireContext()` replacements, same verbose code | Rewrite to declarative — 40-50% fewer lines |
| **Dynamic lists** | Keep manual `mutableListOf<TextView>()` pools (P-003) | `LazyColumn` / `items()` — no pool management |
| **State binding** | Keep 15+ `lateinit var` references per screen | `collectAsState()` — zero manual binding |
| **`buildContent()` methods** | Stay at 150-180 lines each | Become 60-90 line `@Composable` functions |
| **New screens** | Still requires imperative View building | Just write a `@Composable` function |
| **Fragment lifecycle traps** | `viewLifecycleOwner` vs `this`, `onDestroyView` nullification | No Fragment lifecycle — Composable follows Activity |
| **Long-term** | Fragments are maintenance mode; Compose is Google's investment | Aligns with Android's future direction |
| **Effort** | Medium (restructure) | Medium-Large (rewrite UI) — but you only do it once |

### Why the effort delta is smaller than it appears

1. **No XML to convert** — The hardest part of Compose migration for most apps (converting XML layouts) doesn't exist here. Every screen is already code.

2. **Translation is mechanical** — The imperative patterns map 1:1 to Compose:
   - `LinearLayout(VERTICAL)` + `addView()` → `Column { }`
   - `LinearLayout(HORIZONTAL)` + `addView()` → `Row { }`
   - `ScrollView` + `LinearLayout` → `Column(Modifier.verticalScroll(...))`
   - `TextView.apply { text=; textSize=; setTextColor() }` → `Text(text, fontSize=, color=)`
   - `GradientDrawable().apply { setColor(); cornerRadius= }` → `Modifier.background(color, RoundedCornerShape())`
   - `View.GONE` / `View.VISIBLE` → `if (condition) { Content() }`

3. **ViewModels are unchanged** — All 3 ViewModels already use `StateFlow`. Compose just calls `.collectAsState()` instead of `.collect { renderSnapshot(it) }`. The ViewModel layer requires zero changes.

4. **minSdk 31** — Full Compose support with no compatibility concerns.

5. **Koin already works with Compose** — `koinViewModel()` is a drop-in replacement for `by viewModel()`.

### What you get that Fragments don't give you

- **Automatic recomposition** — When StateFlow emits, only the changed Text/Row re-renders. No manual `connectivityValue.text = ...` assignments.
- **No view reference management** — The 15 `lateinit var` fields in DiagnosticsActivity and the `errorTextViews` pool pattern disappear completely.
- **Composable preview** — `@Preview` functions let you see UI without running the app.
- **Built-in animation** — `AnimatedVisibility`, `animateContentSize()`, crossfade transitions — all trivial.
- **Testable UI** — `composeTestRule.onNodeWithText("REFRESH").performClick()` — no Robolectric required.

---

## 3. Architecture Overview

```
MainActivity (single Activity)
├── setContent { AppNavHost() }
│
AppNavHost (Compose NavHost)
├── SplashScreen        → auto-navigate after 500ms
├── LauncherScreen      → route based on registration state
├── ProvisioningScreen  → QR/manual registration flow
├── AppScaffold         → ModalNavigationDrawer + TopBar + BottomBar
│   ├── SiteOverviewScreen
│   ├── DiagnosticsScreen
│   └── SettingsScreen
└── DecommissionedScreen → dead-end, reprovision only
```

Key design: `AppScaffold` wraps only the 3 main screens that need the drawer. Splash, Launcher, Provisioning, and Decommissioned are standalone full-screen composables — no drawer, no top bar.

---

## 4. Dependency Changes

### Add to `app/build.gradle.kts`

```kotlin
// In android { } block:
buildFeatures {
    buildConfig = true
    compose = true         // NEW
}
composeOptions {
    kotlinCompilerExtensionVersion = "1.5.15"  // Match Kotlin version
}

// In dependencies { }:

// Compose BOM (manages all Compose versions consistently)
val composeBom = platform("androidx.compose:compose-bom:2025.02.00")
implementation(composeBom)

// Compose UI
implementation("androidx.compose.ui:ui")
implementation("androidx.compose.ui:ui-tooling-preview")
implementation("androidx.compose.material3:material3")
debugImplementation("androidx.compose.ui:ui-tooling")

// Compose Navigation
implementation("androidx.navigation:navigation-compose:2.8.6")

// Compose + Activity integration
implementation("androidx.activity:activity-compose:1.10.1")

// Compose + Lifecycle (collectAsState)
implementation("androidx.lifecycle:lifecycle-runtime-compose:2.8.7")

// Compose + Koin
implementation("io.insert-koin:koin-androidx-compose:4.0.1")

// Compose testing
androidTestImplementation(composeBom)
androidTestImplementation("androidx.compose.ui:ui-test-junit4")
debugImplementation("androidx.compose.ui:ui-test-manifest")
```

### Add Compose compiler plugin

In the root `build.gradle.kts` or app-level, ensure the Kotlin Compose compiler plugin is applied. For Kotlin 2.0+:
```kotlin
plugins {
    id("org.jetbrains.kotlin.plugin.compose") version "2.0.21"  // match kotlin version
}
```

### Removals (after migration complete)

```kotlin
// Remove these — no longer needed:
// implementation("androidx.appcompat:appcompat:1.7.0")         // Compose replaces AppCompat
// implementation("androidx.drawerlayout:drawerlayout:...")      // transitive, but unused
```

AppCompat can be kept temporarily if the QR scanner (ZXing) requires `AppCompatActivity`.

---

## 5. New Files to Create

### 5.1 `ui/theme/Theme.kt` (~60 lines)

Compose theme matching current Puma branding:

```kotlin
// Colors
val PumaGreen = Color(0xFF007A33)
val PumaRed = Color(0xFFE30613)
val PumaDarkRed = Color(0xFFB8050F)
val StatusGreen = Color(0xFF2E7D32)
val StatusYellow = Color(0xFFF9A825)
val StatusRed = Color(0xFFC62828)
val TextPrimary = Color(0xFF1A1A1A)
val TextLabel = Color(0xFF4A4A4A)
val TextGray = Color(0xFF9E9E9E)

// Theme
@Composable
fun FccEdgeAgentTheme(content: @Composable () -> Unit) {
    MaterialTheme(
        colorScheme = lightColorScheme(
            primary = PumaRed,
            secondary = PumaGreen,
            background = Color.White,
            surface = Color.White,
        ),
        content = content,
    )
}
```

### 5.2 `ui/theme/Components.kt` (~80 lines)

Shared composable building blocks used across screens:

```kotlin
@Composable fun SectionHeader(title: String)           // Green bold header
@Composable fun DataRow(label: String, value: String, valueColor: Color)
@Composable fun PumaButton(text: String, onClick: () -> Unit, ...)
@Composable fun PumaOutlinedButton(text: String, onClick: () -> Unit, ...)
@Composable fun PumaLogo(modifier: Modifier)
```

These replace the duplicated `makeSectionHeader()`, `makeRow()`, `makeValue()` helper methods currently copied in every Activity.

### 5.3 `ui/navigation/AppNavHost.kt` (~90 lines)

The navigation graph in Kotlin (no XML):

```kotlin
@Composable
fun AppNavHost(navController: NavHostController) {
    NavHost(navController, startDestination = "splash") {
        composable("splash") { SplashScreen(navController) }
        composable("launcher") { LauncherScreen(navController) }
        composable("provisioning?reason={reason}",
            arguments = listOf(navArgument("reason") { defaultValue = "" })
        ) { backStackEntry ->
            ProvisioningScreen(navController, backStackEntry.arguments?.getString("reason"))
        }
        composable("decommissioned") { DecommissionedScreen(navController) }

        // Main screens wrapped in shared drawer scaffold
        composable("siteOverview") { AppScaffold(navController, "siteOverview") { SiteOverviewScreen(navController) } }
        composable("diagnostics") { AppScaffold(navController, "diagnostics") { DiagnosticsScreen(navController) } }
        composable("settings") { AppScaffold(navController, "settings") { SettingsScreen(navController) } }
    }
}
```

### 5.4 `ui/navigation/AppScaffold.kt` (~120 lines)

Single shared drawer + top bar + bottom bar for the 3 main screens:

```kotlin
@Composable
fun AppScaffold(
    navController: NavHostController,
    currentRoute: String,
    content: @Composable () -> Unit,
) {
    val drawerState = rememberDrawerState(DrawerValue.Closed)
    val scope = rememberCoroutineScope()

    ModalNavigationDrawer(
        drawerState = drawerState,
        drawerContent = {
            ModalDrawerSheet {
                PumaLogo(...)
                Text("Edge Agent", ...)
                HorizontalDivider(color = PumaGreen)
                NavigationDrawerItem("Site Overview",  selected = currentRoute == "siteOverview", onClick = { ... })
                NavigationDrawerItem("Diagnostics",    selected = currentRoute == "diagnostics",  onClick = { ... })
                NavigationDrawerItem("Settings",       selected = currentRoute == "settings",     onClick = { ... })
                // DiagnosticsScreen adds File Logs / Share Logs via extra items
            }
        },
    ) {
        Scaffold(
            topBar = {
                TopAppBar(
                    title = { Text(titleForRoute(currentRoute)) },
                    navigationIcon = { IconButton(onClick = { scope.launch { drawerState.open() } }) { Icon(Icons.Default.Menu, ...) } },
                    colors = TopAppBarDefaults.topAppBarColors(containerColor = Color.White),
                )
            },
        ) { innerPadding ->
            // innerPadding already handles status bar + nav bar insets
            Box(Modifier.padding(innerPadding)) {
                content()
            }
        }
    }
}
```

This is the **entire drawer + insets + top bar solution** — ~120 lines replacing `NavigationDrawerHelper.kt` (271 lines) plus per-Activity wiring code.

### 5.5 Screen Files (one per current Activity)

| Current File (Activity) | New File (Composable) | Est. Lines | Change |
|---|---|---|---|
| `SplashActivity.kt` (93 lines) | `ui/screens/SplashScreen.kt` | ~40 | -57% |
| `LauncherActivity.kt` (89 lines) | `ui/screens/LauncherScreen.kt` | ~40 | -55% |
| `ProvisioningActivity.kt` (722 lines) | `ui/screens/ProvisioningScreen.kt` | ~450 | -38% |
| `SiteOverviewActivity.kt` (596 lines) | `ui/screens/SiteOverviewScreen.kt` | ~300 | -50% |
| `DiagnosticsActivity.kt` (629 lines) | `ui/screens/DiagnosticsScreen.kt` | ~320 | -49% |
| `SettingsActivity.kt` (776 lines) | `ui/screens/SettingsScreen.kt` | ~450 | -42% |
| `DecommissionedActivity.kt` (215 lines) | `ui/screens/DecommissionedScreen.kt` | ~100 | -53% |
| `NavigationDrawerHelper.kt` (271 lines) | **Deleted** (replaced by AppScaffold) | 0 | -100% |
| — | `ui/theme/Theme.kt` (new) | ~60 | — |
| — | `ui/theme/Components.kt` (new) | ~80 | — |
| — | `ui/navigation/AppNavHost.kt` (new) | ~90 | — |
| — | `ui/navigation/AppScaffold.kt` (new) | ~120 | — |
| — | `ReprovisionHelper.kt` (new) | ~50 | — |
| **Total current**: 3,391 lines | **Total after**: ~2,100 lines | | **-38%** |

### 5.6 `ReprovisionHelper.kt` (~50 lines)

Same as Option A — extracts duplicated credential-clearing logic:

```kotlin
object ReprovisionHelper {
    suspend fun execute(context: Context, ...) {
        // 1. Stop foreground service (AF-004)
        // 2. Clear BufferDatabase (AF-013)
        // 3. Clear Keystore
        // 4. Clear EncryptedPrefs
        // 5. Clear LocalOverrideManager (AF-052)
    }
}
```

### 5.7 `MainActivity.kt` (~30 lines)

Dramatically simpler than the Fragment version:

```kotlin
class MainActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        WindowCompat.setDecorFitsSystemWindows(window, false)
        setContent {
            FccEdgeAgentTheme {
                val navController = rememberNavController()
                AppNavHost(navController)
            }
        }
    }
}
```

---

## 6. Files to Delete (after migration)

| File | Lines | Reason |
|---|---|---|
| `SplashActivity.kt` | 93 | Replaced by `SplashScreen.kt` |
| `LauncherActivity.kt` | 89 | Replaced by `LauncherScreen.kt` |
| `ProvisioningActivity.kt` | 722 | Replaced by `ProvisioningScreen.kt` |
| `SiteOverviewActivity.kt` | 596 | Replaced by `SiteOverviewScreen.kt` |
| `DiagnosticsActivity.kt` | 629 | Replaced by `DiagnosticsScreen.kt` |
| `SettingsActivity.kt` | 776 | Replaced by `SettingsScreen.kt` |
| `DecommissionedActivity.kt` | 215 | Replaced by `DecommissionedScreen.kt` |
| `NavigationDrawerHelper.kt` | 271 | Replaced by `AppScaffold.kt` |
| **Total deleted**: 3,391 lines | | |

---

## 7. Files to Modify

### 7.1 `AndroidManifest.xml`

Replace all 7 `<activity>` declarations with single `MainActivity`:

```xml
<activity
    android:name=".ui.MainActivity"
    android:exported="true"
    android:configChanges="orientation|screenSize|screenLayout"
    android:theme="@style/Theme.FccEdgeAgent">
    <intent-filter>
        <action android:name="android.intent.action.MAIN" />
        <category android:name="android.intent.category.LAUNCHER" />
    </intent-filter>
</activity>
```

### 7.2 `app/build.gradle.kts`

Add `compose = true` to `buildFeatures`, add Compose dependencies (see Section 4).

### 7.3 `AppModule.kt` (DI)

Replace `viewModel { }` with Koin Compose-compatible registration. The ViewModels themselves are unchanged — only how they're retrieved changes:

```kotlin
// Before (Activity/Fragment):
val vm: DiagnosticsViewModel by viewModel()

// After (Compose):
val vm: DiagnosticsViewModel = koinViewModel()
```

The `single { }` registrations for services, DAOs, etc. remain identical.

### 7.4 `themes.xml`

- Remove `Theme.FccEdgeAgent.Launcher` (no separate Activity)
- Remove `Theme.FccEdgeAgent.Splash` (splash handled in Compose)
- Simplify to a single theme for the window chrome:

```xml
<style name="Theme.FccEdgeAgent" parent="Theme.AppCompat.Light.NoActionBar">
    <item name="android:statusBarColor">@android:color/transparent</item>
    <item name="android:navigationBarColor">@android:color/transparent</item>
</style>
```

Transparent system bars because Compose `Scaffold` handles insets via `innerPadding`.

### 7.5 `EdgeAgentForegroundService.kt`

No changes. Static helper methods (`requestImmediateConfigPoll`) use Context, not Activity. Called from Compose via `LocalContext.current`.

### 7.6 ViewModels (no changes)

All 3 ViewModels (`DiagnosticsViewModel`, `SiteOverviewViewModel`, `ProvisioningViewModel`) use `StateFlow` which Compose consumes via `.collectAsState()`. Zero ViewModel code changes.

---

## 8. Migration Steps (Ordered)

### Phase 1: Infrastructure — no visible change

| Task | Description | Files |
|---|---|---|
| 1.1 | Add Compose plugin + dependencies to `build.gradle.kts` | `build.gradle.kts` |
| 1.2 | Add `compose = true` to `buildFeatures` | `build.gradle.kts` |
| 1.3 | Create `ui/theme/Theme.kt` with Puma colour scheme | New file |
| 1.4 | Create `ui/theme/Components.kt` with shared composables (`SectionHeader`, `DataRow`, `PumaButton`, etc.) | New file |
| 1.5 | Create `ReprovisionHelper.kt` extracting shared credential-clearing logic | New file |
| 1.6 | Verify: app compiles, existing tests pass, no visible change | — |

### Phase 2: Navigation shell — app launches but screens are stubs

| Task | Description | Files |
|---|---|---|
| 2.1 | Create `MainActivity.kt` (ComponentActivity + `setContent`) | New file |
| 2.2 | Create `ui/navigation/AppNavHost.kt` with all routes declared, stub composables | New file |
| 2.3 | Create `ui/navigation/AppScaffold.kt` with `ModalNavigationDrawer` + `Scaffold` | New file |
| 2.4 | Create stub `SplashScreen.kt` (logo + delay + navigate) | New file |
| 2.5 | Create stub `LauncherScreen.kt` (routing logic) | New file |
| 2.6 | Update `AndroidManifest.xml`: add MainActivity, keep old Activities temporarily | `AndroidManifest.xml` |
| 2.7 | Point launcher intent-filter at `MainActivity` | `AndroidManifest.xml` |
| 2.8 | Verify: app launches via Compose, routes to correct stub screen | — |

### Phase 3: Migrate leaf screens (low complexity)

| Task | Description | Files |
|---|---|---|
| 3.1 | Convert `SplashActivity` → `SplashScreen.kt` composable | New file, delete old |
| 3.2 | Convert `LauncherActivity` → `LauncherScreen.kt` composable (registration state check + route) | New file, delete old |
| 3.3 | Convert `DecommissionedActivity` → `DecommissionedScreen.kt` composable + `ReprovisionHelper` | New file, delete old |
| 3.4 | Verify: Splash → Launcher → routing works. Decommissioned → Reprovision works. | — |

### Phase 4: Migrate main screens (high complexity, highest value)

| Task | Description | Files |
|---|---|---|
| 4.1 | Convert `SiteOverviewActivity` → `SiteOverviewScreen.kt` | New file |
|  | — Convert `buildContent()` imperative UI → Compose Column/Row/Text | |
|  | — Convert pump card rendering → `PumpCard` composable + `LazyColumn` | |
|  | — Replace manual `PumpCardViewHolder` pool with Compose `items()` | |
|  | — Bottom bar → `BottomAppBar` or `Row` inside Scaffold | |
|  | — ViewModel: `snapshot.collectAsState()` replaces `lifecycleScope.collect` | |
| 4.2 | Convert `DiagnosticsActivity` → `DiagnosticsScreen.kt` | New file |
|  | — Convert all diagnostic sections → Compose `DataRow` composables | |
|  | — Replace `errorTextViews` pool → `LazyColumn` with `items(snapshot.recentLogs)` | |
|  | — Convert `showLogsDialog()` → Compose `AlertDialog` composable | |
|  | — Convert share flow → `rememberLauncherForActivityResult` | |
|  | — Bottom bar with REFRESH / SETTINGS / SHARE | |
| 4.3 | Convert `SettingsActivity` → `SettingsScreen.kt` | New file |
|  | — Convert form fields → Compose `OutlinedTextField` composables | |
|  | — Convert validation logic → ViewModel or local state | |
|  | — Convert override indicators → conditional `Text` with colour | |
|  | — Convert Cloud API Routes → `LazyColumn` | |
|  | — Reprovision → `ReprovisionHelper` + `navController.navigate("provisioning")` | |
|  | — `FLAG_SECURE` toggle via `DisposableEffect` | |
| 4.4 | Delete old Activity files: `SiteOverviewActivity`, `DiagnosticsActivity`, `SettingsActivity` | Delete 3 files |
| 4.5 | Verify: Full drawer navigation across all 3 main screens | — |

### Phase 5: Migrate provisioning (most complex screen)

| Task | Description | Files |
|---|---|---|
| 5.1 | Convert `ProvisioningActivity` → `ProvisioningScreen.kt` | New file |
|  | — Convert method selection panel → Compose state-driven visibility | |
|  | — Convert manual entry form → `OutlinedTextField` composables | |
|  | — Convert QR scan launcher → `rememberLauncherForActivityResult(ScanContract())` | |
|  | — Convert camera permission → `rememberLauncherForActivityResult(RequestPermission())` | |
|  | — Convert progress/error state → ViewModel `registrationState.collectAsState()` | |
|  | — Convert back press handling → `BackHandler` composable | |
|  | — `FLAG_SECURE` toggle via `DisposableEffect` | |
|  | — Success navigation: `navController.navigate("siteOverview") { popUpTo(0) { inclusive = true } }` | |
| 5.2 | Start foreground service on success → `LocalContext.current.startForegroundService()` | |
| 5.3 | Delete `ProvisioningActivity.kt` | Delete file |
| 5.4 | Verify: Full QR + manual provisioning flow, rotation, process death | — |

### Phase 6: Cleanup

| Task | Description | Files |
|---|---|---|
| 6.1 | Delete `NavigationDrawerHelper.kt` | Delete file |
| 6.2 | Remove all old `<activity>` declarations from `AndroidManifest.xml` | Modify |
| 6.3 | Remove `Theme.FccEdgeAgent.Launcher` and `Theme.FccEdgeAgent.Splash` from `themes.xml` | Modify |
| 6.4 | Update `AppModule.kt` if needed for Compose ViewModel retrieval | Modify |
| 6.5 | Update/rewrite unit tests: Robolectric Activity tests → Compose test rules | Modify test files |
| 6.6 | Remove `appcompat` dependency if no longer needed (check ZXing) | `build.gradle.kts` |
| 6.7 | Full regression test (see checklist in Section 13) | — |

---

## 9. Compose Equivalents for Current Patterns

### Imperative View → Compose mapping

| Current Pattern | Compose Equivalent |
|---|---|
| `LinearLayout(VERTICAL) { addView(...) }` | `Column { ... }` |
| `LinearLayout(HORIZONTAL) { addView(...) }` | `Row { ... }` |
| `ScrollView + LinearLayout` | `Column(Modifier.verticalScroll(rememberScrollState()))` |
| `TextView.apply { text=; textSize=; setTextColor() }` | `Text(text, fontSize=.sp, color=)` |
| `Button.apply { setOnClickListener {} }` | `Button(onClick = {}) { Text(...) }` |
| `EditText.apply { hint=; inputType= }` | `OutlinedTextField(value, onValueChange, placeholder=)` |
| `ImageView.setImageResource(R.drawable.x)` | `Image(painterResource(R.drawable.x))` |
| `GradientDrawable + cornerRadius` | `Modifier.background(color, RoundedCornerShape(8.dp))` |
| `View.GONE / View.VISIBLE` | `if (condition) { Composable() }` |
| `setPadding(dp(16), dp(8), dp(16), dp(8))` | `Modifier.padding(horizontal = 16.dp, vertical = 8.dp)` |
| `layoutParams = LayoutParams(0, WRAP, 1f)` | `Modifier.weight(1f)` |
| `Toast.makeText(ctx, msg, LENGTH_SHORT).show()` | Snackbar via `SnackbarHostState` or keep Toast |
| `AlertDialog.Builder(ctx).setTitle().show()` | `AlertDialog(onDismissRequest, title, text, ...)` |
| `lifecycleScope.launch { flow.collect { renderSnapshot(it) } }` | `val snapshot by viewModel.snapshot.collectAsState()` |
| `lateinit var connectivityValue: TextView` + manual `.text =` | Just use `snapshot.connectivityState` directly in `Text()` |
| `mutableListOf<TextView>()` pool + grow/shrink | `items(snapshot.recentLogs) { entry -> LogEntry(entry) }` |
| `registerForActivityResult(...)` | `rememberLauncherForActivityResult(...)` |
| `onBackPressed { ... }` | `BackHandler { ... }` |
| `window.setFlags(FLAG_SECURE)` | `DisposableEffect(Unit) { activity.window.setFlags(...); onDispose { clear } }` |

### State management transformation

```kotlin
// BEFORE: 15 lateinit vars + manual renderSnapshot()
private lateinit var connectivityValue: TextView
private lateinit var bufferDepthValue: TextView
// ... 13 more ...

private fun renderSnapshot(snapshot: DiagnosticsSnapshot) {
    connectivityValue.text = snapshot.connectivityState.name
    connectivityValue.setTextColor(when(snapshot.connectivityState) { ... })
    bufferDepthValue.text = snapshot.bufferDepth.toString()
    // ... 50 more manual binding lines ...
}

// AFTER: zero lateinit vars, zero manual binding
@Composable
fun DiagnosticsScreen(...) {
    val snapshot by viewModel.snapshot.collectAsState()
    snapshot?.let { data ->
        DataRow("State:", data.connectivityState.name, connectivityColor(data.connectivityState))
        DataRow("Buffer Depth:", data.bufferDepth.toString(), bufferColor(data.bufferDepth))
        // Each DataRow is 1 line, not 5
    }
}
```

---

## 10. Security Considerations

### FLAG_SECURE

Toggle per-route using `DisposableEffect` in the screen composable:

```kotlin
@Composable
fun SettingsScreen(...) {
    val activity = LocalContext.current as ComponentActivity
    DisposableEffect(Unit) {
        activity.window.setFlags(FLAG_SECURE, FLAG_SECURE)
        onDispose { activity.window.clearFlags(FLAG_SECURE) }
    }
    // ... screen content ...
}
```

Applied to: `ProvisioningScreen`, `SettingsScreen`.
Not applied to: `SplashScreen`, `LauncherScreen`, `SiteOverviewScreen`, `DiagnosticsScreen`, `DecommissionedScreen`.

### Credential state

Compose `rememberSaveable` replaces `onSaveInstanceState`. The AF-001 / LR-012 guards (not persisting tokens/passwords) are enforced by simply NOT using `rememberSaveable` for those fields:

```kotlin
// Saved across rotation:
var fccIp by rememberSaveable { mutableStateOf("") }

// NOT saved (re-entered after process death):
var fccAccessCode by remember { mutableStateOf("") }  // remember, not rememberSaveable
```

### Reprovision ordering

Unchanged — `ReprovisionHelper.execute()` preserves the critical AF-004/AF-013/AF-052 sequence.

---

## 11. Drawer Behaviour Per Screen

| Route | Drawer | Top Bar | Title | Extra Menu Items |
|---|---|---|---|---|
| `splash` | Locked closed | Hidden | — | — |
| `launcher` | Locked closed | Hidden | — | — |
| `provisioning` | Locked closed | Hidden | — | — |
| `decommissioned` | Locked closed | Hidden | — | — |
| `siteOverview` | Unlocked | Visible | "Site Overview" | — |
| `diagnostics` | Unlocked | Visible | "Diagnostics" | File Logs, Share Logs |
| `settings` | Unlocked | Visible | "Settings" | — |

Implemented by `AppScaffold` — screens outside the scaffold have no drawer at all.

---

## 12. Risk Assessment

| Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|
| Compose learning curve | Low-Medium | Medium | Mechanical translation from existing imperative code; patterns are well-documented |
| QR scanner (ZXing) Compose interop | Low | Medium | `rememberLauncherForActivityResult(ScanContract())` works identically to Activity version |
| APK size increase from Compose | Low | Low | ~2-3 MB increase; acceptable for this app class |
| Build time increase from Compose compiler | Low | Low | Incremental compilation; minSdk 31 avoids desugaring overhead |
| EncryptedSharedPreferences async reads (AT-008) | Low | Medium | Keep `Dispatchers.IO` reads in ViewModel; Compose just observes the StateFlow |
| Koin + Compose integration issues | Low | Low | `koin-androidx-compose` is mature; `koinViewModel()` is drop-in |
| FLAG_SECURE timing during navigation | Low | Low | `DisposableEffect` cleanup is synchronous; window between routes is sub-frame |
| Test migration (Robolectric → Compose tests) | Medium | Medium | Compose test rules are simpler than Robolectric; net improvement |

---

## 13. Manual Testing Checklist

- [ ] Splash → Launcher → SiteOverview (registered device)
- [ ] Splash → Launcher → Provisioning (unregistered device)
- [ ] Splash → Launcher → Decommissioned (decommissioned device)
- [ ] QR provisioning → SiteOverview (back stack cleared, cannot go back)
- [ ] Manual provisioning → SiteOverview (back stack cleared)
- [ ] Drawer opens/closes on SiteOverview, Diagnostics, Settings
- [ ] Drawer locked/hidden on Splash, Launcher, Provisioning, Decommissioned
- [ ] Menu items highlight correctly per screen
- [ ] Navigate between all 3 main screens via drawer
- [ ] Navigate between all 3 main screens via bottom bar buttons
- [ ] Settings → Reprovision → Provisioning (back stack cleared)
- [ ] Decommissioned → Reprovision → Provisioning (back stack cleared)
- [ ] Rotation preserves form state on Settings and Provisioning
- [ ] Rotation does NOT restore token/password fields (AF-001, LR-012)
- [ ] Share Logs flow from Diagnostics (FileProvider + chooser)
- [ ] File Logs dialog from Diagnostics drawer menu
- [ ] Back button closes drawer before navigating back
- [ ] Back button from Decommissioned does nothing
- [ ] System nav bar does not overlap bottom buttons on any screen
- [ ] Status bar insets correct on all screens
- [ ] FLAG_SECURE active on Provisioning and Settings (try screenshot)
- [ ] FLAG_SECURE NOT active on SiteOverview and Diagnostics
- [ ] Foreground service starts on first launch after provisioning
- [ ] Config refresh button triggers immediate poll
- [ ] Pump cards render correctly on SiteOverview
- [ ] Pump status live updates work (auto-refresh)
- [ ] Diagnostics auto-refresh updates values every 5 seconds
- [ ] Settings Save & Reconnect validation works (invalid IP, duplicate ports)
- [ ] Settings override indicators show correctly
- [ ] Camera permission request works for QR scanning
