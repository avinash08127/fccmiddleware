package com.fccmiddleware.edge.buffer

import androidx.room.TypeConverter
import com.fccmiddleware.edge.adapter.common.PreAuthStatus

class PreAuthStatusConverters {
    @TypeConverter
    fun fromStatus(status: PreAuthStatus): String = status.name

    @TypeConverter
    fun toStatus(value: String): PreAuthStatus = PreAuthStatus.valueOf(value)
}
