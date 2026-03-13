package com.fccmiddleware.edge.config

import android.content.Context
import android.content.SharedPreferences
import androidx.test.core.app.ApplicationProvider
import com.fccmiddleware.edge.security.KeystoreManager
import io.mockk.every
import io.mockk.mockk
import io.mockk.verify
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config

@RunWith(RobolectricTestRunner::class)
@Config(sdk = [31])
class LocalOverrideManagerSecurityTest {

    private lateinit var context: Context
    private lateinit var keystoreManager: KeystoreManager
    private lateinit var manager: LocalOverrideManager

    @Before
    fun setUp() {
        context = ApplicationProvider.getApplicationContext()
        context.deleteSharedPreferences("fcc_local_overrides")
        keystoreManager = mockk(relaxed = true)
        manager = LocalOverrideManager(context, keystoreManager)
    }

    @After
    fun tearDown() {
        context.deleteSharedPreferences("fcc_local_overrides")
    }

    @Test
    fun `saveOverride stores FCC credential via keystore-backed blob`() {
        every {
            keystoreManager.storeSecret(KeystoreManager.ALIAS_FCC_CRED, "secret-code")
        } returns byteArrayOf(1, 2, 3)
        every {
            keystoreManager.retrieveSecret(KeystoreManager.ALIAS_FCC_CRED, any())
        } returns "secret-code"

        manager.saveOverride(LocalOverrideManager.KEY_FCC_CREDENTIAL, "secret-code")

        verify(exactly = 1) {
            keystoreManager.storeSecret(KeystoreManager.ALIAS_FCC_CRED, "secret-code")
        }
        assertNull(prefs().getString(LocalOverrideManager.KEY_FCC_CREDENTIAL, null))
        assertEquals("secret-code", manager.fccCredential)
    }

    @Test
    fun `legacy plaintext FCC credential is migrated on read`() {
        prefs().edit().putString(LocalOverrideManager.KEY_FCC_CREDENTIAL, "legacy-secret").commit()
        every {
            keystoreManager.storeSecret(KeystoreManager.ALIAS_FCC_CRED, "legacy-secret")
        } returns byteArrayOf(9, 8, 7)

        assertEquals("legacy-secret", manager.fccCredential)

        verify(exactly = 1) {
            keystoreManager.storeSecret(KeystoreManager.ALIAS_FCC_CRED, "legacy-secret")
        }
        assertNull(prefs().getString(LocalOverrideManager.KEY_FCC_CREDENTIAL, null))
    }

    @Test
    fun `clearOverride deletes FCC keystore alias`() {
        manager.clearOverride(LocalOverrideManager.KEY_FCC_CREDENTIAL)

        verify(exactly = 1) {
            keystoreManager.deleteKey(KeystoreManager.ALIAS_FCC_CRED)
        }
    }

    private fun prefs(): SharedPreferences {
        val field = LocalOverrideManager::class.java.getDeclaredField("prefs")
        field.isAccessible = true
        return field.get(manager) as SharedPreferences
    }
}
