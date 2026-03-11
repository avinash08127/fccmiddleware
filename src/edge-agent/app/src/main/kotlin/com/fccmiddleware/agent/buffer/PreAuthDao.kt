package com.fccmiddleware.agent.buffer

import androidx.room.Dao
import androidx.room.Insert
import androidx.room.OnConflictStrategy
import androidx.room.Query
import androidx.room.Update

@Dao
interface PreAuthDao {

    @Insert(onConflict = OnConflictStrategy.REPLACE)
    suspend fun insert(record: PreAuthRecordEntity): Long

    @Update
    suspend fun update(record: PreAuthRecordEntity)

    @Query("SELECT * FROM pre_auth_records WHERE id = :id")
    suspend fun getById(id: String): PreAuthRecordEntity?

    @Query("SELECT * FROM pre_auth_records WHERE pump_number = :pumpNumber AND status IN ('PENDING', 'AUTHORIZED', 'DISPENSING')")
    suspend fun findActiveByPump(pumpNumber: Int): List<PreAuthRecordEntity>

    @Query("SELECT * FROM pre_auth_records WHERE status = :status ORDER BY created_at DESC")
    suspend fun findAllByStatus(status: String): List<PreAuthRecordEntity>
}
