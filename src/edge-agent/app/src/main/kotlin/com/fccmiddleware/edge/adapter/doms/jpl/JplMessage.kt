package com.fccmiddleware.edge.adapter.doms.jpl

import kotlinx.serialization.Serializable

/** JPL protocol message: every message has a name, optional sub-code, and JSON data map. */
@Serializable
data class JplMessage(
    val name: String,
    val subCode: Int? = null,
    val data: Map<String, String> = emptyMap(),
)
