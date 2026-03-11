package com.fccmiddleware.agent.buffer

import androidx.room.Dao
import androidx.room.Insert
import androidx.room.OnConflictStrategy
import androidx.room.Query
import androidx.room.Update

@Dao
interface TransactionBufferDao {

    @Insert(onConflict = OnConflictStrategy.IGNORE)
    suspend fun insert(transaction: BufferedTransactionEntity): Long

    @Update
    suspend fun update(transaction: BufferedTransactionEntity)

    @Query("SELECT * FROM buffered_transactions WHERE sync_status = :status ORDER BY created_at ASC LIMIT :limit")
    suspend fun findAllByStatus(status: String, limit: Int): List<BufferedTransactionEntity>

    @Query("SELECT * FROM buffered_transactions WHERE fcc_transaction_id = :fccTransactionId AND site_code = :siteCode")
    suspend fun getByFccTransactionId(fccTransactionId: String, siteCode: String): BufferedTransactionEntity?

    @Query("SELECT COUNT(*) FROM buffered_transactions WHERE sync_status = :status")
    suspend fun countByStatus(status: String): Int

    @Query("DELETE FROM buffered_transactions WHERE sync_status = 'ARCHIVED' AND created_at < :cutoffDate")
    suspend fun deleteArchivedBefore(cutoffDate: String): Int
}
