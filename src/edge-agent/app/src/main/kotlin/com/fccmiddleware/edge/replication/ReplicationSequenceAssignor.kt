package com.fccmiddleware.edge.replication

import java.util.concurrent.atomic.AtomicLong

/**
 * Thread-safe monotonic sequence generator for replication ordering.
 *
 * The primary agent assigns a replication sequence number to every mutation
 * (transaction insert/update, pre-auth change) before it is persisted to Room.
 * Standby agents use the sequence to determine replication lag and detect gaps.
 *
 * Initialized from the current high-water-mark stored in Room. After
 * initialization, [nextSequence] is safe to call from any thread.
 */
class ReplicationSequenceAssignor {

    private val counter = AtomicLong(0)

    /**
     * Initialize the counter from the current maximum sequence stored in the database.
     * Must be called once during startup before any calls to [nextSequence].
     *
     * @param currentMax the highest replication sequence present in Room, or 0 if empty.
     */
    fun initialize(currentMax: Long) {
        counter.set(currentMax)
    }

    /**
     * Returns the next monotonically increasing sequence number.
     * Thread-safe — multiple coroutines may call concurrently.
     */
    fun nextSequence(): Long = counter.incrementAndGet()

    /** Current high-water-mark (the last assigned sequence). */
    val currentSequence: Long
        get() = counter.get()
}
