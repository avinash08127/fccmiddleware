package com.fccmiddleware.edge.logging

import kotlinx.coroutines.ThreadContextElement
import kotlin.coroutines.CoroutineContext

/**
 * AF-002: Coroutine context element that scopes a correlation ID to the current
 * coroutine. When the coroutine is dispatched to a thread, this element copies
 * the correlation ID into the ThreadLocal on [updateThreadContext] and restores
 * the previous value on [restoreThreadContext].
 *
 * Usage in Ktor interceptors:
 * ```
 * withContext(CorrelationIdElement(correlationId)) {
 *     // All log calls within this coroutine will use the scoped correlationId
 * }
 * ```
 */
class CorrelationIdElement(
    private val correlationId: String?,
) : ThreadContextElement<String?> {

    companion object Key : CoroutineContext.Key<CorrelationIdElement>

    override val key: CoroutineContext.Key<CorrelationIdElement> get() = Key

    override fun updateThreadContext(context: CoroutineContext): String? {
        val threadLocal = AppLogger.correlationIdLocal ?: return null
        val previous = threadLocal.get()
        threadLocal.set(correlationId)
        return previous
    }

    override fun restoreThreadContext(context: CoroutineContext, oldState: String?) {
        val threadLocal = AppLogger.correlationIdLocal ?: return
        threadLocal.set(oldState)
    }
}
