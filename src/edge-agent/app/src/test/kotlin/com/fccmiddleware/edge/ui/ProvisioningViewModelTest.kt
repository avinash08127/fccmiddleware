package com.fccmiddleware.edge.ui

import android.app.Application
import com.fccmiddleware.edge.registration.RegistrationHandler
import com.fccmiddleware.edge.security.EncryptedPrefsManager
import com.fccmiddleware.edge.sync.CloudApiClient
import io.mockk.every
import io.mockk.mockk
import io.mockk.verify
import org.junit.Assert.assertEquals
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config

@RunWith(RobolectricTestRunner::class)
@Config(manifest = Config.NONE, sdk = [31])
class ProvisioningViewModelTest {

    private val application = mockk<Application>(relaxed = true)
    private val cloudApiClient = mockk<CloudApiClient>(relaxed = true)
    private val encryptedPrefs = mockk<EncryptedPrefsManager>(relaxed = true)
    private val registrationHandler = mockk<RegistrationHandler>(relaxed = true)

    private lateinit var viewModel: ProvisioningViewModel

    @Before
    fun setUp() {
        viewModel = ProvisioningViewModel(
            application = application,
            cloudApiClient = cloudApiClient,
            encryptedPrefs = encryptedPrefs,
            registrationHandler = registrationHandler,
        )
    }

    @Test
    fun `resolveDeviceSerialNumber uses android id when available`() {
        val result = viewModel.resolveDeviceSerialNumber("android-id-123")

        assertEquals("android-id-123", result)
        verify(exactly = 0) { encryptedPrefs.getOrCreateProvisioningDeviceSerialFallback() }
    }

    @Test
    fun `resolveDeviceSerialNumber uses persisted fallback when android id missing`() {
        every { encryptedPrefs.getOrCreateProvisioningDeviceSerialFallback() } returns "unknown-stable42"

        val result = viewModel.resolveDeviceSerialNumber(null)

        assertEquals("unknown-stable42", result)
        verify(exactly = 1) { encryptedPrefs.getOrCreateProvisioningDeviceSerialFallback() }
    }
}
