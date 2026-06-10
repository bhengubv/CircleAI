// Json.kt
//
// Tiny hand-rolled JSON adapters for the catalog. The package already
// depends on kotlinx-serialization-json (test scope) — but using it at
// runtime would bump the dep tier. For 1.5.0 we get by with parsing
// only the fields the client + registry need.

package com.bhengubv.circleai.catalog

import com.bhengubv.circleai.models.BundleFile
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.boolean
import kotlinx.serialization.json.contentOrNull
import kotlinx.serialization.json.intOrNull
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import kotlinx.serialization.json.longOrNull
import java.time.Instant

private val json: Json = Json {
    ignoreUnknownKeys = true
    isLenient = true
}

internal fun registryToJson(reg: ModelRegistry): String {
    val sb = StringBuilder()
    sb.append('{')
    sb.append("\"registry_url\":").append(quote(reg.registryUrl)).append(',')
    sb.append("\"last_updated\":").append(quote(reg.lastUpdated.toString())).append(',')
    sb.append("\"models\":[")
    for ((i, m) in reg.models.withIndex()) {
        if (i > 0) sb.append(',')
        sb.append('{')
        sb.append("\"name\":").append(quote(m.name)).append(',')
        sb.append("\"version\":").append(quote(m.version)).append(',')
        sb.append("\"quantization\":").append(quote(m.quantization)).append(',')
        sb.append("\"repo\":").append(quote(m.repo ?: "")).append(',')
        sb.append("\"total_bytes\":").append(m.totalBytes).append(',')
        sb.append("\"min_ram_gb\":").append(m.minRamGb).append(',')
        sb.append("\"min_storage_gb\":").append(m.minStorageGb).append(',')
        sb.append("\"quality_rank\":").append(m.qualityRank).append(',')
        sb.append("\"bundle_files\":[")
        for ((j, f) in m.bundleFiles.withIndex()) {
            if (j > 0) sb.append(',')
            sb.append('{')
            sb.append("\"name\":").append(quote(f.name)).append(',')
            sb.append("\"sha256\":").append(quote(f.sha256)).append(',')
            sb.append("\"size_bytes\":").append(f.sizeBytes)
            sb.append('}')
        }
        sb.append("]}")
    }
    sb.append("]}")
    return sb.toString()
}

internal fun parseRegistryJson(raw: String): ModelRegistry {
    val obj = json.parseToJsonElement(raw).jsonObject
    val models = mutableListOf<ModelEntry>()
    obj["models"]?.jsonArray?.let { arr ->
        for (el in arr) {
            val m = el.jsonObject
            val files = m["bundle_files"]?.jsonArray?.map { f ->
                val fo = f.jsonObject
                BundleFile(
                    name = fo["name"]?.jsonPrimitive?.contentOrNull ?: "",
                    sha256 = fo["sha256"]?.jsonPrimitive?.contentOrNull ?: "",
                    sizeBytes = fo["size_bytes"]?.jsonPrimitive?.longOrNull ?: 0L,
                )
            } ?: emptyList()
            models.add(
                ModelEntry(
                    name = m["name"]?.jsonPrimitive?.contentOrNull ?: "",
                    version = m["version"]?.jsonPrimitive?.contentOrNull ?: "",
                    quantization = m["quantization"]?.jsonPrimitive?.contentOrNull ?: "",
                    repo = m["repo"]?.jsonPrimitive?.contentOrNull?.takeIf { it.isNotEmpty() },
                    totalBytes = m["total_bytes"]?.jsonPrimitive?.longOrNull ?: 0L,
                    minRamGb = m["min_ram_gb"]?.jsonPrimitive?.contentOrNull?.toDoubleOrNull() ?: 0.0,
                    minStorageGb = m["min_storage_gb"]?.jsonPrimitive?.contentOrNull?.toDoubleOrNull() ?: 0.0,
                    qualityRank = m["quality_rank"]?.jsonPrimitive?.intOrNull ?: 0,
                    bundleFiles = files,
                )
            )
        }
    }
    return ModelRegistry(
        registryUrl = obj["registry_url"]?.jsonPrimitive?.contentOrNull ?: "",
        lastUpdated = obj["last_updated"]?.jsonPrimitive?.contentOrNull?.let(Instant::parse)
            ?: Instant.now(),
        models = models,
    )
}

internal fun extractListingItems(raw: String): List<Map<String, String>> {
    val out = mutableListOf<Map<String, String>>()
    val obj = json.parseToJsonElement(raw).jsonObject
    val dataNode = obj["Data"]?.jsonObject ?: return out
    val arr = dataNode["Model"]?.jsonArray ?: return out
    for (el in arr) {
        val o = el.jsonObject
        val map = mutableMapOf<String, String>()
        listOf("Name", "Path", "Revision", "Quantization").forEach { k ->
            o[k]?.jsonPrimitive?.contentOrNull?.let { map[k] = it }
        }
        out.add(map)
    }
    return out
}

internal fun extractFileItems(raw: String): List<BundleFile> {
    val out = mutableListOf<BundleFile>()
    val obj = json.parseToJsonElement(raw).jsonObject
    val dataNode = obj["Data"]?.jsonObject ?: return out
    val arr = dataNode["Files"]?.jsonArray ?: return out
    for (el in arr) {
        val o = el.jsonObject
        val name = (o["Path"] ?: o["Name"])?.jsonPrimitive?.contentOrNull ?: continue
        out.add(
            BundleFile(
                name = name,
                sha256 = o["Sha256"]?.jsonPrimitive?.contentOrNull ?: "",
                sizeBytes = o["Size"]?.jsonPrimitive?.longOrNull ?: 0L,
            )
        )
    }
    return out
}

private fun quote(s: String): String {
    val sb = StringBuilder(s.length + 2)
    sb.append('"')
    for (c in s) {
        when (c) {
            '"' -> sb.append("\\\"")
            '\\' -> sb.append("\\\\")
            '\n' -> sb.append("\\n")
            '\r' -> sb.append("\\r")
            '\t' -> sb.append("\\t")
            else -> if (c.code < 0x20) {
                sb.append(String.format("\\u%04x", c.code))
            } else sb.append(c)
        }
    }
    sb.append('"')
    return sb.toString()
}
