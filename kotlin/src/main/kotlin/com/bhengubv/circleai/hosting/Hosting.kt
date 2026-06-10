// Hosting.kt
//
// IAIObserver + AIOptions.

package com.bhengubv.circleai.hosting

import com.bhengubv.circleai.catalog.ModelScopeCatalogClient
import com.bhengubv.circleai.device.IDeviceContext
import com.bhengubv.circleai.models.ChatResponse
import com.bhengubv.circleai.models.UpgradeInfo
import com.bhengubv.circleai.selector.ChatCapability

interface IAIObserver {
    suspend fun onStartedAsync() {}
    suspend fun onStoppedAsync() {}
    suspend fun onChatCompletedAsync(response: ChatResponse) {}
    suspend fun onStreamStartedAsync(modelId: String) {}
    suspend fun onStreamCompletedAsync(modelId: String, tokenCount: Int) {}
    suspend fun onToolInvokedAsync(toolName: String, success: Boolean) {}
    suspend fun onModelFetchingAsync(modelId: String, autoSelected: Boolean) {}
    suspend fun onUpgradeAvailableAsync(upgrade: UpgradeInfo) {}
}

/** Default no-op observer — hosts can subclass and override what they need. */
open class AIObserverBase : IAIObserver

data class AIOptions(
    val modelId: String? = null,
    val modelPath: String? = null,

    val systemPrompt: String = "You are B!, a helpful on-device assistant.",
    val contextSize: Int? = null,
    val threadCount: Int? = null,
    val warmOnStart: Boolean = true,

    val deviceContext: IDeviceContext? = null,
    val catalogClient: ModelScopeCatalogClient? = null,
    val requiredCapabilities: Int = ChatCapability.DEFAULT,

    val agenticMaxIterations: Int? = null,

    val observer: IAIObserver? = null,

    val checkForUpgradesOnStart: Boolean = false,
    val modelStorageDirectory: String? = null,
)
