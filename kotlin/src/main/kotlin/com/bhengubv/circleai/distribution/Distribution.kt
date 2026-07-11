// Distribution.kt
//
// Kotlin port of CircleAI.Distribution — the C# reference is the EXACT spec
// (Contracts.cs, NullImplementations.cs, UbiquityRails.cs,
// UbiquityRailsMissingDefaults.cs).
//
// The distribution + "77 ubiquity rails" surface: app-store submission, signed
// delta updates, OEM / carrier preload catalogues, onboarding, trust, pricing,
// localisation, hardware fallbacks (USSD/SMS), services, regulator, recovery,
// failure modes, cost, network effects, and cultural rails. Each rail is a
// small interface + default; hosts wire real integrations against them.
//
// C# -> Kotlin conventions:
//   ValueTask / Task      -> suspend fun (where the C# member is async)
//   ReadOnlyMemory<byte>  -> ByteArray
//   byte[]                -> ByteArray
//   Uri                   -> java.net.URI
//   TimeSpan              -> java.time.Duration
//   DateTimeOffset        -> java.time.Instant
//   decimal               -> java.math.BigDecimal
//   Stream                -> ByteArray (the export bundle bytes)
//   ConcurrentDictionary  -> synchronized MutableMap
//   HMACSHA256 / SHA256   -> javax.crypto.Mac / java.security.MessageDigest

package com.bhengubv.circleai.distribution

import java.math.BigDecimal
import java.net.URI
import java.security.MessageDigest
import java.security.SecureRandom
import java.time.Duration
import java.time.Instant
import javax.crypto.Mac
import javax.crypto.spec.SecretKeySpec

// ===========================================================================
// Contracts.cs — file sync + peer advertiser
// ===========================================================================

data class FileMetadata(val contentHash: String, val name: String, val sizeBytes: Long)
data class Peer(val peerId: String, val endpoint: String, val availableHashes: List<String>)

interface IFileSync {
    val backendId: String
    suspend fun has(contentHash: String): Boolean
    suspend fun fetch(contentHash: String): ByteArray?
    suspend fun announce(metadata: FileMetadata, payload: ByteArray)
}

interface IPeerAdvertiser {
    val backendId: String
    suspend fun discover(): List<Peer>
}

class NullFileSync private constructor() : IFileSync {
    override val backendId: String get() = "null"
    override suspend fun has(contentHash: String): Boolean = false
    override suspend fun fetch(contentHash: String): ByteArray? = null
    override suspend fun announce(metadata: FileMetadata, payload: ByteArray) {}

    companion object {
        val Instance = NullFileSync()
    }
}

class NullPeerAdvertiser private constructor() : IPeerAdvertiser {
    override val backendId: String get() = "null"
    override suspend fun discover(): List<Peer> = emptyList()

    companion object {
        val Instance = NullPeerAdvertiser()
    }
}

// ===========================================================================
// UbiquityRails.cs — DISTRIBUTION
// ===========================================================================

data class AppStorePackage(
    val storeName: String,
    val packagePath: String,
    val version: String,
    val metadata: Map<String, String>,
)

interface IAppStoreSubmitter {
    suspend fun submit(pkg: AppStorePackage): Boolean
}

data class DeltaUpdate(
    val channel: String,
    val fromVersion: String,
    val toVersion: String,
    val payload: ByteArray,
    val signature: ByteArray,
) {
    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is DeltaUpdate) return false
        return channel == other.channel &&
            fromVersion == other.fromVersion &&
            toVersion == other.toVersion &&
            payload.contentEquals(other.payload) &&
            signature.contentEquals(other.signature)
    }

    override fun hashCode(): Int {
        var result = channel.hashCode()
        result = 31 * result + fromVersion.hashCode()
        result = 31 * result + toVersion.hashCode()
        result = 31 * result + payload.contentHashCode()
        result = 31 * result + signature.contentHashCode()
        return result
    }
}

interface ISignedDeltaUpdater {
    suspend fun apply(update: DeltaUpdate): Boolean
}

interface IOemPreloadCatalog {
    val partners: List<String>
}

class DefaultOemPreloadCatalog : IOemPreloadCatalog {
    override val partners: List<String> = listOf("Tecno", "Itel", "Samsung mid-tier", "Xiaomi", "Huawei")
}

interface ICarrierPreloadCatalog {
    val carriers: List<String>
}

class DefaultCarrierPreloadCatalog : ICarrierPreloadCatalog {
    override val carriers: List<String> = listOf("MTN", "Vodacom", "Cell C", "Telkom", "Safaricom", "Airtel")
}

interface IPwaFallback {
    val pwaUrl: URI
}

class DefaultPwaFallback : IPwaFallback {
    override val pwaUrl: URI = URI("https://app.circle.ai")
}

interface ISideloadChannel {
    val formats: List<String>
}

class DefaultSideloadChannel : ISideloadChannel {
    override val formats: List<String> = listOf("APK", "IPA", "MSIX")
}

interface ILinuxRepoFanout {
    val repos: List<String>
}

class DefaultLinuxRepoFanout : ILinuxRepoFanout {
    override val repos: List<String> = listOf("apt", "yum", "pacman", "brew", "flatpak", "snap")
}

// ===========================================================================
// ONBOARDING
// ===========================================================================

data class OnboardingSession(
    val sessionId: String,
    val phoneNumber: String,
    val biometricEnrolled: Boolean,
    val timeToActive: Duration,
)

interface IPhonePinBiometricOnboarding {
    suspend fun start(phoneNumber: String): OnboardingSession
    suspend fun complete(sessionId: String, pin: String, biometricOk: Boolean)
}

interface INoManualFirstRun {
    suspend fun show(): String
}

interface IVoiceLedSetup {
    /** Mother-tongue voice-led setup. */
    suspend fun run(motherTongue: String): Boolean
}

data class PersonalityChoice(val name: String)

interface IAiPersonalityWizard {
    val presets: List<PersonalityChoice>
    suspend fun select(sessionId: String, choice: PersonalityChoice)
}

class DefaultAiPersonalityWizard : IAiPersonalityWizard {
    private val selections = HashMap<String, PersonalityChoice>()
    private val lock = Any()

    override val presets: List<PersonalityChoice> = listOf(
        PersonalityChoice("formal"),
        PersonalityChoice("warm"),
        PersonalityChoice("playful"),
        PersonalityChoice("professional"),
    )

    override suspend fun select(sessionId: String, choice: PersonalityChoice) {
        require(sessionId.isNotBlank()) { "sessionId required" }
        if (presets.none { it.name.equals(choice.name, ignoreCase = true) }) {
            throw IllegalStateException("Unknown personality '${choice.name}'.")
        }
        synchronized(lock) { selections[sessionId] = choice }
    }

    fun selected(sessionId: String): PersonalityChoice? = synchronized(lock) { selections[sessionId] }
}

interface IPersonalDataImport {
    suspend fun import(sessionId: String, source: String)
}

data class HouseholdMember(val memberId: String, val displayName: String, val role: String)

interface IFamilyOnboarding {
    suspend fun createHousehold(ownerId: String, members: List<HouseholdMember>)
}

// ===========================================================================
// TRUST
// ===========================================================================

interface IThirdPartySecurityAuditPublisher {
    val reportUrl: URI
}

class DefaultThirdPartySecurityAuditPublisher : IThirdPartySecurityAuditPublisher {
    override val reportUrl: URI = URI("https://trust.circle.ai/audit")
}

interface IComplianceCertifications {
    val certifications: List<String>
}

class DefaultComplianceCertifications : IComplianceCertifications {
    override val certifications: List<String> = listOf("SOC 2 Type II", "ISO 27001", "ISO 27701")
}

interface IBugBountyChannel {
    val platform: String
    val submissionUrl: URI
}

class DefaultBugBountyChannel : IBugBountyChannel {
    override val platform: String get() = "HackerOne"
    override val submissionUrl: URI = URI("https://h1.com/circleai")
}

interface IPrivacyRegulationCompliance {
    val laws: List<String>
}

class DefaultPrivacyRegulationCompliance : IPrivacyRegulationCompliance {
    override val laws: List<String> = listOf("GDPR", "POPIA", "CCPA", "LGPD")
}

interface IVerifiablePrivacyProof {
    val buildIsReproducible: Boolean
    val sourceUrl: String
}

class DefaultVerifiablePrivacyProof : IVerifiablePrivacyProof {
    override val buildIsReproducible: Boolean get() = true
    override val sourceUrl: String get() = "https://github.com/bhengubv/CircleAI"
}

data class TransparencyReceipt(
    val callId: String,
    val actionsTaken: List<String>,
    val dataEgress: List<String>,
    val costUsd: BigDecimal,
)

interface IPerCallTransparency {
    suspend fun receiptFor(callId: String): TransparencyReceipt
}

// ===========================================================================
// PRICING
// ===========================================================================

data class PricingTier(
    val name: String,
    val monthlyPriceLocal: BigDecimal,
    val currency: String,
    val features: List<String>,
)

interface IPricingMatrix {
    val all: List<PricingTier>
}

class DefaultPricingMatrix : IPricingMatrix {
    override val all: List<PricingTier> = listOf(
        PricingTier("free", BigDecimal("0"), "ZAR", listOf("Local chat", "Family memory cap")),
        PricingTier("paid", BigDecimal("19"), "ZAR", listOf("Unlimited cloud calls", "Priority routing")),
        PricingTier("family", BigDecimal("49"), "ZAR", listOf("Up to 6 members")),
        PricingTier("stokvel", BigDecimal("99"), "ZAR", listOf("Group memory", "Group reporting")),
        PricingTier("enterprise", BigDecimal("200"), "ZAR", listOf("Dedicated brain", "SLA")),
    )
}

interface IPluginMarketplaceRevenueShare {
    val authorShare: Double
    val verifiedSafeShare: Double
}

class DefaultPluginMarketplaceRevenueShare : IPluginMarketplaceRevenueShare {
    override val authorShare: Double get() = 0.70
    override val verifiedSafeShare: Double get() = 0.50
}

interface ICarrierRevenueShare {
    val carrierShare: Double
}

class DefaultCarrierRevenueShare : ICarrierRevenueShare {
    override val carrierShare: Double get() = 0.25
}

// ===========================================================================
// LOCALISATION
// ===========================================================================

interface ICurrencyFormatter {
    fun format(amount: BigDecimal, isoCurrencyCode: String): String
}

class DefaultCurrencyFormatter : ICurrencyFormatter {
    override fun format(amount: BigDecimal, isoCurrencyCode: String): String =
        "${amount.setScale(2, java.math.RoundingMode.HALF_UP).toPlainString()} $isoCurrencyCode"
}

interface IPhoneNumberFormatter {
    fun format(e164: String, countryCodeIsoAlpha2: String): String
}

class DefaultPhoneNumberFormatter : IPhoneNumberFormatter {
    override fun format(e164: String, countryCodeIsoAlpha2: String): String = e164
}

interface ICulturalNameRecogniser {
    fun recognisesLanguage(isoLanguage: String): Boolean
}

class DefaultCulturalNameRecogniser : ICulturalNameRecogniser {
    private val supported = hashSetOf(
        "zul", "xho", "tsn", "sot", "yor", "ibo", "twi", "swa", "hin", "ben",
    ).mapTo(HashSet()) { it.lowercase() }

    override fun recognisesLanguage(isoLanguage: String): Boolean = supported.contains(isoLanguage.lowercase())
}

interface ICulturalGreetings {
    fun greetingFor(isoLanguage: String): String
}

class DefaultCulturalGreetings : ICulturalGreetings {
    override fun greetingFor(isoLanguage: String): String = when (isoLanguage) {
        "zul", "zu" -> "Sawubona"
        "xho", "xh" -> "Molo"
        "yor" -> "Ẹ kú àárọ̀"
        "hin" -> "नमस्ते"
        else -> "Hello"
    }
}

interface ISaServiceConnectors {
    val banks: List<String>
    val wallets: List<String>
}

class DefaultSaServiceConnectors : ISaServiceConnectors {
    override val banks: List<String> = listOf("Capitec", "FNB", "Standard", "Absa", "Nedbank")
    override val wallets: List<String> = listOf("PayFast", "SnapScan")
}

interface ICrossBorderCorridors {
    val corridors: List<String>
}

class DefaultCrossBorderCorridors : ICrossBorderCorridors {
    override val corridors: List<String> = listOf("SADC", "ECOWAS", "EAC")
}

interface IIndigenousKnowledgeProtocols {
    fun requiresElderReview(isoLanguage: String): Boolean
}

class DefaultIndigenousKnowledgeProtocols : IIndigenousKnowledgeProtocols {
    override fun requiresElderReview(isoLanguage: String): Boolean = true
}

// ===========================================================================
// HARDWARE
// ===========================================================================

interface ILowRamPhoneSupport {
    fun supportsRamMb(ramMb: Int): Boolean
}

class DefaultLowRamPhoneSupport : ILowRamPhoneSupport {
    override fun supportsRamMb(ramMb: Int): Boolean = ramMb >= 512
}

interface ILowCpuOptimization {
    fun supportsClockMhz(clockMhz: Int): Boolean
}

class DefaultLowCpuOptimization : ILowCpuOptimization {
    override fun supportsClockMhz(clockMhz: Int): Boolean = clockMhz >= 600
}

interface IOfflineQueuedOperation {
    suspend fun enqueue(operationJson: String)
    val pending: List<String>
    fun tryDequeue(): String?
}

class DefaultOfflineQueuedOperation : IOfflineQueuedOperation {
    private val q = ArrayDeque<String>()
    private val lock = Any()

    override suspend fun enqueue(operationJson: String) {
        require(operationJson.isNotBlank()) { "operationJson required" }
        synchronized(lock) { q.addLast(operationJson) }
    }

    override val pending: List<String>
        get() = synchronized(lock) { q.toList() }

    override fun tryDequeue(): String? = synchronized(lock) { q.removeFirstOrNull() }
}

interface ISmsFallback {
    suspend fun answerViaSms(phoneNumber: String, question: String)
    val sent: List<Triple<String, String, Instant>>
}

class DefaultSmsFallback(
    private val delivery: (suspend (String, String) -> Unit)? = null,
) : ISmsFallback {
    private val sentList = ArrayList<Triple<String, String, Instant>>()
    private val lock = Any()

    override suspend fun answerViaSms(phoneNumber: String, question: String) {
        require(phoneNumber.isNotBlank()) { "phoneNumber required" }
        require(question.isNotBlank()) { "question required" }
        synchronized(lock) { sentList.add(Triple(phoneNumber, question, Instant.now())) }
        delivery?.invoke(phoneNumber, question)
    }

    override val sent: List<Triple<String, String, Instant>>
        get() = synchronized(lock) { sentList.toList() }
}

interface IUssdFallback {
    suspend fun respond(ussdSession: String, input: String): String
}

class DefaultUssdFallback : IUssdFallback {
    // Session -> last-shown-menu key.
    private val sessions = HashMap<String, String>()
    private val lock = Any()

    private data class Menu(val prompt: String, val routes: Map<String, String>)

    private val menus: Map<String, Menu> = mapOf(
        "root" to Menu(
            "CircleAI:\n1. Balance\n2. Ask AI\n3. Help",
            mapOf("1" to "balance", "2" to "ask", "3" to "help"),
        ),
        "balance" to Menu("Balance: R0.00\n0. Back", mapOf("0" to "root")),
        "ask" to Menu("Type question, then send.\n0. Back", mapOf("0" to "root")),
        "help" to Menu("Dial *120*CIRCLE# anytime.\n0. Back", mapOf("0" to "root")),
    )

    override suspend fun respond(ussdSession: String, input: String): String {
        require(ussdSession.isNotBlank()) { "ussdSession required" }
        val current = synchronized(lock) { sessions.getOrPut(ussdSession) { "root" } }
        val menu = menus[current] ?: run {
            synchronized(lock) { sessions[ussdSession] = "root" }
            return menus.getValue("root").prompt
        }
        val next = menu.routes[input.trim()]
        if (next != null) {
            synchronized(lock) { sessions[ussdSession] = next }
            return menus.getValue(next).prompt
        }
        return menu.prompt
    }
}

interface IKaiOsSupport {
    val isCompiled: Boolean
}

class DefaultKaiOsSupport : IKaiOsSupport {
    override val isCompiled: Boolean get() = true
}

// ===========================================================================
// SERVICES
// ===========================================================================

interface IWhatsAppIntegration {
    suspend fun send(phoneNumber: String, message: String)
    val outbox: List<Triple<String, String, Instant>>
}

class DefaultWhatsAppIntegration(
    private val send: (suspend (String, String) -> Unit)? = null,
) : IWhatsAppIntegration {
    private val out = ArrayList<Triple<String, String, Instant>>()
    private val lock = Any()
    private val e164 = Regex("""^\+?[1-9]\d{6,14}$""")

    override suspend fun send(phoneNumber: String, message: String) {
        require(phoneNumber.isNotBlank()) { "phoneNumber required" }
        require(message.isNotBlank()) { "message required" }
        require(e164.matches(phoneNumber)) { "Invalid E.164 phone '$phoneNumber'." }
        synchronized(lock) { out.add(Triple(phoneNumber, message, Instant.now())) }
        this.send?.invoke(phoneNumber, message)
    }

    override val outbox: List<Triple<String, String, Instant>>
        get() = synchronized(lock) { out.toList() }
}

interface ITelegramIntegration {
    suspend fun send(chatId: String, message: String)
    val outbox: List<Triple<String, String, Instant>>
}

class DefaultTelegramIntegration(
    private val send: (suspend (String, String) -> Unit)? = null,
) : ITelegramIntegration {
    private val out = ArrayList<Triple<String, String, Instant>>()
    private val lock = Any()

    override suspend fun send(chatId: String, message: String) {
        require(chatId.isNotBlank()) { "chatId required" }
        require(message.isNotBlank()) { "message required" }
        synchronized(lock) { out.add(Triple(chatId, message, Instant.now())) }
        this.send?.invoke(chatId, message)
    }

    override val outbox: List<Triple<String, String, Instant>>
        get() = synchronized(lock) { out.toList() }
}

interface IEmailConnectorRegistry {
    val providers: List<String>
}

class DefaultEmailConnectorRegistry : IEmailConnectorRegistry {
    override val providers: List<String> = listOf("Gmail", "Outlook", "iCloud", "ProtonMail", "Yandex", "Yahoo", "IMAP")
}

interface ICalendarConnectorRegistry {
    val providers: List<String>
}

class DefaultCalendarConnectorRegistry : ICalendarConnectorRegistry {
    override val providers: List<String> = listOf("Google", "Outlook", "Apple", "Yahoo", "CalDAV")
}

interface ICrmConnectorRegistry {
    val providers: List<String>
}

class DefaultCrmConnectorRegistry : ICrmConnectorRegistry {
    override val providers: List<String> = listOf("HubSpot", "Salesforce", "Pipedrive", "Zoho", "Bitrix")
}

interface IAccountingConnectorRegistry {
    val providers: List<String>
}

class DefaultAccountingConnectorRegistry : IAccountingConnectorRegistry {
    override val providers: List<String> = listOf("Xero", "Sage", "QuickBooks", "Wave", "Manager.io")
}

interface IBankingConnectorRegistry {
    val providers: List<String>
}

class DefaultBankingConnectorRegistry : IBankingConnectorRegistry {
    override val providers: List<String> = listOf("open-banking-ZA", "open-banking-NG", "open-banking-KE")
}

// ===========================================================================
// REGULATOR
// ===========================================================================

interface ISarbSandboxStatus {
    val approved: Boolean
}

class DefaultSarbSandboxStatus : ISarbSandboxStatus {
    override val approved: Boolean get() = false
}

interface IIcasaApprovalStatus {
    val approved: Boolean
}

class DefaultIcasaApprovalStatus : IIcasaApprovalStatus {
    override val approved: Boolean get() = false
}

interface IGlobalRegulatorEngagement {
    val activeJurisdictions: List<String>
}

class DefaultGlobalRegulatorEngagement : IGlobalRegulatorEngagement {
    override val activeJurisdictions: List<String> = listOf("ZA", "NG", "KE", "US", "CA", "UK", "EU")
}

interface ITaxInvoiceRegistry {
    val schemes: List<String>
}

class DefaultTaxInvoiceRegistry : ITaxInvoiceRegistry {
    override val schemes: List<String> = listOf("VAT", "GST", "Sales Tax", "DST")
}

interface ILawfulInterceptCompliance {
    val posture: String
}

class DefaultLawfulInterceptCompliance : ILawfulInterceptCompliance {
    override val posture: String get() = "Money decryptable to law, comms permanently blind"
}

// ===========================================================================
// RECOVERY
// ===========================================================================

interface ILostDeviceFlow {
    suspend fun remoteWipe(deviceId: String)
    fun isWiped(deviceId: String): Boolean
}

class DefaultLostDeviceFlow : ILostDeviceFlow {
    private val wiped = HashMap<String, Instant>()
    private val lock = Any()

    override suspend fun remoteWipe(deviceId: String) {
        require(deviceId.isNotBlank()) { "deviceId required" }
        synchronized(lock) { wiped[deviceId] = Instant.now() }
    }

    override fun isWiped(deviceId: String): Boolean = synchronized(lock) { wiped.containsKey(deviceId) }
}

interface IInheritanceProtocol {
    suspend fun designate(ownerId: String, designeeId: String)
    fun designeeFor(ownerId: String): String?
}

class DefaultInheritanceProtocol : IInheritanceProtocol {
    private val designees = HashMap<String, String>()
    private val lock = Any()

    override suspend fun designate(ownerId: String, designeeId: String) {
        require(ownerId.isNotBlank()) { "ownerId required" }
        require(designeeId.isNotBlank()) { "designeeId required" }
        require(ownerId != designeeId) { "Designee cannot equal owner." }
        synchronized(lock) { designees[ownerId] = designeeId }
    }

    override fun designeeFor(ownerId: String): String? = synchronized(lock) { designees[ownerId] }
}

interface IVerifiableWipe {
    suspend fun wipeAndCertify(ownerId: String): ByteArray
}

class DefaultVerifiableWipe : IVerifiableWipe {
    private val rng = SecureRandom()

    override suspend fun wipeAndCertify(ownerId: String): ByteArray {
        require(ownerId.isNotBlank()) { "ownerId required" }
        // Certificate = SHA-256 over "wipe|ownerId|iso-timestamp|nonce".
        val nonce = ByteArray(16).also { rng.nextBytes(it) }
        val payload = "wipe|$ownerId|${Instant.now()}|${java.util.Base64.getEncoder().encodeToString(nonce)}"
        return MessageDigest.getInstance("SHA-256").digest(payload.toByteArray(Charsets.UTF_8))
    }
}

interface IDataPortabilityExport {
    /** Returns the export bundle bytes. */
    suspend fun export(ownerId: String): ByteArray
}

class DefaultDataPortabilityExport : IDataPortabilityExport {
    override suspend fun export(ownerId: String): ByteArray {
        require(ownerId.isNotBlank()) { "ownerId required" }
        val json = buildString {
            append("{")
            append("\"owner_id\":").append(jsonString(ownerId)).append(",")
            append("\"exported_at\":").append(jsonString(Instant.now().toString())).append(",")
            append("\"schema\":\"circleai/portability/v1\",")
            append("\"note\":\"Host overrides export to stream actual user data (memory, contacts, transcripts).\"")
            append("}")
        }
        return json.toByteArray(Charsets.UTF_8)
    }

    private fun jsonString(s: String): String =
        "\"" + s.replace("\\", "\\\\").replace("\"", "\\\"") + "\""
}

interface IAccountCompromiseRecovery {
    suspend fun begin(ownerId: String)
    fun inRecovery(ownerId: String): Boolean
    fun complete(ownerId: String)
}

class DefaultAccountCompromiseRecovery : IAccountCompromiseRecovery {
    private val active = HashMap<String, Instant>()
    private val lock = Any()

    override suspend fun begin(ownerId: String) {
        require(ownerId.isNotBlank()) { "ownerId required" }
        synchronized(lock) { active[ownerId] = Instant.now() }
    }

    override fun inRecovery(ownerId: String): Boolean = synchronized(lock) { active.containsKey(ownerId) }
    override fun complete(ownerId: String) {
        synchronized(lock) { active.remove(ownerId) }
    }
}

// ===========================================================================
// FAILURE MODES
// ===========================================================================

interface IBrainUnreachableMode {
    val localTakeoverEnabled: Boolean
}

class DefaultBrainUnreachableMode : IBrainUnreachableMode {
    override val localTakeoverEnabled: Boolean get() = true
}

interface INoInternetCacheTarget {
    val hitRateTarget: Float
}

class DefaultNoInternetCacheTarget : INoInternetCacheTarget {
    override val hitRateTarget: Float get() = 0.80f
}

interface IStorageFullDegradationPolicy {
    val degradeOrder: String
}

class DefaultStorageFullDegradationPolicy : IStorageFullDegradationPolicy {
    override val degradeOrder: String get() = "cache > old-snapshots > chat-history > nothing"
}

interface IImpairedUserMode {
    suspend fun engage(ownerId: String)
    fun isEngaged(ownerId: String): Boolean
    suspend fun disengage(ownerId: String)
}

class DefaultImpairedUserMode : IImpairedUserMode {
    private val engaged = HashSet<String>()
    private val lock = Any()

    override suspend fun engage(ownerId: String) {
        require(ownerId.isNotBlank()) { "ownerId required" }
        synchronized(lock) { engaged.add(ownerId) }
    }

    override fun isEngaged(ownerId: String): Boolean = synchronized(lock) { engaged.contains(ownerId) }
    override suspend fun disengage(ownerId: String) {
        synchronized(lock) { engaged.remove(ownerId) }
    }
}

interface IAbusiveEnvironmentMode {
    suspend fun engage(ownerId: String)

    /** Test phrase the user can speak to silently invoke abuse-safe mode. Generated per user. */
    fun safetyPhrase(ownerId: String): String
    fun isEngaged(ownerId: String): Boolean
}

class DefaultAbusiveEnvironmentMode : IAbusiveEnvironmentMode {
    private val engaged = HashSet<String>()
    private val phrases = HashMap<String, String>()
    private val lock = Any()
    private val words = arrayOf("thunder", "river", "amber", "field", "rain", "stone", "harbor", "linen")

    override suspend fun engage(ownerId: String) {
        require(ownerId.isNotBlank()) { "ownerId required" }
        synchronized(lock) { engaged.add(ownerId) }
    }

    override fun safetyPhrase(ownerId: String): String {
        require(ownerId.isNotBlank()) { "ownerId required" }
        return synchronized(lock) {
            phrases.getOrPut(ownerId) {
                // Deterministic per-owner safety phrase from a benign vocabulary.
                val h = ownerId.hashCode().toUInt()
                "the ${words[(h % 8u).toInt()]} ${words[((h shr 8) % 8u).toInt()]} is ${words[((h shr 16) % 8u).toInt()]}"
            }
        }
    }

    override fun isEngaged(ownerId: String): Boolean = synchronized(lock) { engaged.contains(ownerId) }
}

interface IPublicDisasterMode {
    val currentState: String
}

class DefaultPublicDisasterMode : IPublicDisasterMode {
    override val currentState: String get() = "normal"
}

// ===========================================================================
// COST
// ===========================================================================

interface ISustainablePerUserCostMath {
    val monthlyRevenuePerUser: BigDecimal
    val monthlyMarginalCostPerUser: BigDecimal
}

class DefaultSustainablePerUserCostMath : ISustainablePerUserCostMath {
    override val monthlyRevenuePerUser: BigDecimal = BigDecimal("19")
    override val monthlyMarginalCostPerUser: BigDecimal = BigDecimal("3.8")
}

interface IPerCallCostCeiling {
    val ceilingUsd: BigDecimal
}

class DefaultPerCallCostCeiling : IPerCallCostCeiling {
    override val ceilingUsd: BigDecimal = BigDecimal("0.40")
}

interface IFreeTierCostCapping {
    val monthlyCapUsd: BigDecimal
}

class DefaultFreeTierCostCapping : IFreeTierCostCapping {
    override val monthlyCapUsd: BigDecimal = BigDecimal("0.20")
}

interface ILocalFirstRouting {
    val preferred: Boolean
}

class DefaultLocalFirstRouting : ILocalFirstRouting {
    override val preferred: Boolean get() = true
}

// ===========================================================================
// NETWORK EFFECTS
// ===========================================================================

interface IReferralProgramme {
    val rewardLocal: BigDecimal
    val currency: String
}

class DefaultReferralProgramme : IReferralProgramme {
    override val rewardLocal: BigDecimal = BigDecimal("19")
    override val currency: String get() = "ZAR"
}

interface IFamilyAiSharing {
    val maxMembers: Int
}

class DefaultFamilyAiSharing : IFamilyAiSharing {
    override val maxMembers: Int get() = 6
}

interface ICrossProviderFederation {
    val enabled: Boolean
}

class DefaultCrossProviderFederation : ICrossProviderFederation {
    override val enabled: Boolean get() = true
}

interface IGroupNetworkEffects {
    val groupTypes: List<String>
}

class DefaultGroupNetworkEffects : IGroupNetworkEffects {
    override val groupTypes: List<String> = listOf("Stokvel", "Church", "Community")
}

interface IUserGrowthFlywheel {
    val mechanic: String
}

class DefaultUserGrowthFlywheel : IUserGrowthFlywheel {
    override val mechanic: String get() = "user invites friend; both get a month free"
}

// ===========================================================================
// CULTURAL
// ===========================================================================

interface IThirdPartyHarmLiability {
    val framework: String
}

class DefaultThirdPartyHarmLiability : IThirdPartyHarmLiability {
    override val framework: String get() = "Operator-of-record indemnity backed by insurance pool"
}

interface IQuietMode {
    suspend fun engage(reason: String, duration: Duration)
    fun isQuietAt(moment: Instant): Boolean
    val activeWindows: List<Triple<String, Instant, Instant>>
}

class DefaultQuietMode : IQuietMode {
    private val windows = ArrayList<Triple<String, Instant, Instant>>()
    private val lock = Any()

    override suspend fun engage(reason: String, duration: Duration) {
        require(reason.isNotBlank()) { "reason required" }
        require(duration > Duration.ZERO) { "duration must be positive" }
        val now = Instant.now()
        synchronized(lock) { windows.add(Triple(reason, now, now.plus(duration))) }
    }

    override fun isQuietAt(moment: Instant): Boolean = synchronized(lock) {
        windows.any { !moment.isBefore(it.second) && !moment.isAfter(it.third) }
    }

    override val activeWindows: List<Triple<String, Instant, Instant>>
        get() {
            val now = Instant.now()
            return synchronized(lock) { windows.filter { !it.third.isBefore(now) } }
        }
}

interface IChildProtectionMode {
    val coppaCompliant: Boolean
    val gdprKCompliant: Boolean
}

class DefaultChildProtectionMode : IChildProtectionMode {
    override val coppaCompliant: Boolean get() = true
    override val gdprKCompliant: Boolean get() = true
}

interface IReligiousAccommodation {
    val supportedModes: List<String>
}

class DefaultReligiousAccommodation : IReligiousAccommodation {
    override val supportedModes: List<String> = listOf("prayer times", "Shabbat mode", "Eid silence")
}

interface IIndigenousDataSovereignty {
    val standard: String
}

class DefaultIndigenousDataSovereignty : IIndigenousDataSovereignty {
    override val standard: String get() = "CARE Principles"
}

interface IPublicTransparency {
    suspend fun linkEvidence(claim: String, evidenceUrl: URI)
    val linked: List<Triple<String, URI, Instant>>
}

class DefaultPublicTransparency : IPublicTransparency {
    private val links = ArrayList<Triple<String, URI, Instant>>()
    private val lock = Any()

    override suspend fun linkEvidence(claim: String, evidenceUrl: URI) {
        require(claim.isNotBlank()) { "claim required" }
        require(evidenceUrl.isAbsolute && (evidenceUrl.scheme == "https" || evidenceUrl.scheme == "http")) {
            "evidenceUrl must be absolute http/https"
        }
        synchronized(lock) { links.add(Triple(claim, evidenceUrl, Instant.now())) }
    }

    override val linked: List<Triple<String, URI, Instant>>
        get() = synchronized(lock) { links.toList() }
}

// ===========================================================================
// UbiquityRailsMissingDefaults.cs — real implementations that had no Default*
// ===========================================================================

/** Default app-store submitter — validates the package and records the submission. */
class DefaultAppStoreSubmitter : IAppStoreSubmitter {
    private val submitted = HashMap<String, AppStorePackage>()
    private val lock = Any()
    private val knownStores = hashSetOf(
        "playstore", "appstore", "galaxy store", "huawei appgallery", "microsoft store", "f-droid",
    )

    override suspend fun submit(pkg: AppStorePackage): Boolean {
        require(pkg.storeName.isNotBlank()) { "StoreName required" }
        require(pkg.packagePath.isNotBlank()) { "PackagePath required" }
        require(pkg.version.isNotBlank()) { "Version required" }
        if (!knownStores.contains(pkg.storeName.lowercase())) return false
        val key = "${pkg.storeName}/${pkg.version}"
        synchronized(lock) { submitted[key] = pkg }
        return true
    }

    val submittedPackages: List<AppStorePackage>
        get() = synchronized(lock) { submitted.values.toList() }
}

/** Signed delta updater — verifies HMAC-SHA256 signature before applying. */
class DefaultSignedDeltaUpdater(hmacKey: ByteArray) : ISignedDeltaUpdater {
    private val hmacKey: ByteArray
    private val channelVersion = HashMap<String, String>()
    private val lock = Any()

    init {
        require(hmacKey.size >= 16) { "hmacKey must be at least 16 bytes" }
        this.hmacKey = hmacKey.copyOf()
    }

    override suspend fun apply(update: DeltaUpdate): Boolean {
        if (update.channel.isBlank() || update.toVersion.isBlank()) return false
        synchronized(lock) {
            val current = channelVersion[update.channel]
            if (current != null && current != update.fromVersion) return false
        }

        // HMAC over Channel|FromVersion|ToVersion|Payload.
        val prefix = "${update.channel}|${update.fromVersion}|${update.toVersion}|".toByteArray(Charsets.UTF_8)
        val msg = prefix + update.payload
        val mac = Mac.getInstance("HmacSHA256").apply { init(SecretKeySpec(hmacKey, "HmacSHA256")) }
        val expected = mac.doFinal(msg)
        if (!fixedTimeEquals(expected, update.signature)) return false
        synchronized(lock) { channelVersion[update.channel] = update.toVersion }
        return true
    }

    fun currentVersion(channel: String): String? = synchronized(lock) { channelVersion[channel] }

    private fun fixedTimeEquals(a: ByteArray, b: ByteArray): Boolean {
        if (a.size != b.size) return false
        var diff = 0
        for (i in a.indices) diff = diff or (a[i].toInt() xor b[i].toInt())
        return diff == 0
    }
}

/** Phone-pin biometric onboarding — real session tracking with PIN strength + biometric flag. */
class DefaultPhonePinBiometricOnboarding : IPhonePinBiometricOnboarding {
    private val sessions = HashMap<String, OnboardingSession>()
    private val pinHashes = HashMap<String, String>()
    private val lock = Any()
    private val e164 = Regex("""^\+?[1-9]\d{6,14}$""")

    override suspend fun start(phoneNumber: String): OnboardingSession {
        require(phoneNumber.isNotBlank()) { "phoneNumber required" }
        require(e164.matches(phoneNumber)) { "Invalid E.164 phone '$phoneNumber'." }
        val sid = java.util.UUID.randomUUID().toString().replace("-", "")
        val session = OnboardingSession(sid, phoneNumber, false, Duration.ZERO)
        synchronized(lock) { sessions[sid] = session }
        return session
    }

    override suspend fun complete(sessionId: String, pin: String, biometricOk: Boolean) {
        require(sessionId.isNotBlank()) { "sessionId required" }
        require(pin.length >= 4 && pin.all { it.isDigit() }) { "PIN must be at least 4 digits" }
        synchronized(lock) {
            val s = sessions[sessionId] ?: throw IllegalStateException("Unknown session $sessionId")
            pinHashes[s.phoneNumber] = hex(sha256((pin + s.phoneNumber).toByteArray(Charsets.UTF_8)))
            sessions[sessionId] = s.copy(biometricEnrolled = biometricOk, timeToActive = Duration.ofMinutes(1))
        }
    }

    fun verifyPin(phoneNumber: String, pin: String): Boolean = synchronized(lock) {
        val h = pinHashes[phoneNumber] ?: return false
        h == hex(sha256((pin + phoneNumber).toByteArray(Charsets.UTF_8)))
    }

    private fun sha256(bytes: ByteArray): ByteArray = MessageDigest.getInstance("SHA-256").digest(bytes)
    private fun hex(bytes: ByteArray): String = bytes.joinToString("") { "%02X".format(it) }
}

/** No-manual first-run — shows a single welcome card. */
class DefaultNoManualFirstRun(welcomeCard: String? = null) : INoManualFirstRun {
    private val welcome = welcomeCard ?: "Welcome to Circle AI. Tap the mic and say hello — that's it."
    override suspend fun show(): String = welcome
}

/** Voice-led setup — accepts supported mother tongues; rejects unknown ones. */
class DefaultVoiceLedSetup : IVoiceLedSetup {
    private val supported = hashSetOf(
        "en", "af", "zu", "xh", "st", "tn", "ts", "ss", "ve", "nr", "nso",
        "sw", "ha", "yo", "ig", "am", "fr", "pt", "ar", "hi", "bn", "es",
    ).mapTo(HashSet()) { it.lowercase() }

    override suspend fun run(motherTongue: String): Boolean {
        require(motherTongue.isNotBlank()) { "motherTongue required" }
        val prefix = motherTongue.split('-')[0]
        return supported.contains(prefix.lowercase())
    }
}

/** Personal data import — accepts a registered source name; records the import. */
class DefaultPersonalDataImport : IPersonalDataImport {
    private val knownSources = hashSetOf(
        "google-takeout", "apple-data-export", "whatsapp-archive", "icloud", "csv", "vcard", "ics",
    ).mapTo(HashSet()) { it.lowercase() }
    private val imports = HashMap<String, MutableList<String>>()
    private val lock = Any()

    override suspend fun import(sessionId: String, source: String) {
        require(sessionId.isNotBlank()) { "sessionId required" }
        require(source.isNotBlank()) { "source required" }
        require(knownSources.contains(source.lowercase())) { "Unsupported import source '$source'." }
        synchronized(lock) { imports.getOrPut(sessionId) { ArrayList() }.add(source) }
    }

    fun importsFor(sessionId: String): List<String> = synchronized(lock) { imports[sessionId]?.toList() ?: emptyList() }
}

/** Family onboarding — household + member roster with role validation. */
class DefaultFamilyOnboarding : IFamilyOnboarding {
    private val validRoles = hashSetOf(
        "owner", "parent", "child", "guardian", "elder", "partner", "guest",
    ).mapTo(HashSet()) { it.lowercase() }
    private val households = HashMap<String, List<HouseholdMember>>()
    private val lock = Any()

    override suspend fun createHousehold(ownerId: String, members: List<HouseholdMember>) {
        require(ownerId.isNotBlank()) { "ownerId required" }
        for (m in members) {
            require(m.memberId.isNotBlank()) { "MemberId required" }
            require(m.displayName.isNotBlank()) { "DisplayName required" }
            require(validRoles.contains(m.role.lowercase())) { "Unknown role '${m.role}'." }
        }
        synchronized(lock) { households[ownerId] = members.toList() }
    }

    fun membersOf(ownerId: String): List<HouseholdMember> = synchronized(lock) { households[ownerId] ?: emptyList() }
}

/** Per-call transparency receipt — real receipt store with summary actions. */
class DefaultPerCallTransparency : IPerCallTransparency {
    private val receipts = HashMap<String, TransparencyReceipt>()
    private val lock = Any()

    fun record(receipt: TransparencyReceipt) {
        require(receipt.callId.isNotBlank()) { "CallId required" }
        synchronized(lock) { receipts[receipt.callId] = receipt }
    }

    override suspend fun receiptFor(callId: String): TransparencyReceipt {
        require(callId.isNotBlank()) { "callId required" }
        return synchronized(lock) { receipts[callId] }
            ?: TransparencyReceipt(callId, emptyList(), emptyList(), BigDecimal.ZERO)
    }
}
