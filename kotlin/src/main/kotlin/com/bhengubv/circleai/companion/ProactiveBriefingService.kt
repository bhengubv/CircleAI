// ProactiveBriefingService.kt
//
// Kotlin port of CircleAI.Companion.ProactiveBriefingService (Phase B5) — the C#
// reference (ProactiveBriefingService.cs) is the EXACT spec. A scheduled service
// that assembles a "what's happening" briefing from registered calendar / email /
// news / weather connectors, summarises it via the LLM, and pushes the result
// through any registered notifier.
//
// The C# service consumes connector contracts from CircleAI.Integration and an
// IAIService from CircleAI.Hosting. Those contracts do not yet exist in the
// Kotlin tree, so — per the porting rules (inject external dependencies behind
// interfaces, no stubs) — the minimal connector/notifier contracts the service
// actually calls are declared here, and the existing inference IChatGenerator is
// used as the LLM (the Kotlin equivalent of IAIService.ChatAsync). The schedule
// logic (TimeUntilNextFire) and the assembly/summarise/deliver flow (fireOnce)
// are ported faithfully. The IHostedService lifecycle becomes a coroutine loop.

package com.bhengubv.circleai.companion

import com.bhengubv.circleai.inference.IChatGenerator
import com.bhengubv.circleai.inference.GenerationOptions
import com.bhengubv.circleai.models.ChatMessage
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.cancelAndJoin
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import java.time.Duration
import java.time.Instant
import java.time.LocalTime
import java.time.ZoneOffset
import java.util.UUID

// ---------------------------------------------------------------------------
// Connector contracts (minimal ports of the CircleAI.Integration shapes used)
// ---------------------------------------------------------------------------

/** A calendar event surfaced into the briefing. */
data class BriefingCalendarEvent(val startUtc: Instant, val title: String, val location: String?)

/** A calendar connector — the briefing lists the next 24h of events. */
interface ICalendarConnector {
    val providerId: String
    val isConfigured: Boolean
    suspend fun listEventsAsync(fromUtc: Instant, toUtc: Instant): List<BriefingCalendarEvent>
}

/** An unread email header surfaced into the briefing. */
data class BriefingEmail(val from: String, val subject: String)

/** An email connector — the briefing lists unread mail. */
interface IEmailConnector {
    val providerId: String
    val isConfigured: Boolean
    suspend fun listUnreadAsync(max: Int): List<BriefingEmail>
}

/** A news item surfaced into the briefing. */
data class BriefingNewsItem(val title: String)

/** A news source — the briefing lists the latest items. */
interface INewsSource {
    val sourceId: String
    val isConfigured: Boolean
    suspend fun fetchLatestAsync(max: Int): List<BriefingNewsItem>
}

/** Current-weather snapshot surfaced into the briefing. */
data class BriefingWeather(val tempC: Double, val condition: String, val feelsLikeC: Double, val windKph: Double)

/** A weather provider — the briefing shows current conditions for a location. */
interface IWeatherProvider {
    val providerId: String
    suspend fun currentAsync(lat: Double, lon: Double): BriefingWeather
}

// ---------------------------------------------------------------------------
// IBriefingNotifier — pluggable delivery channel.
// ---------------------------------------------------------------------------

/** Pluggable notifier — hosts wire WhatsApp, Telegram, SMS, push, etc. */
interface IBriefingNotifier {
    suspend fun deliverAsync(headline: String, body: String, address: String?)
}

// ---------------------------------------------------------------------------
// ProactiveBriefingOptions
// ---------------------------------------------------------------------------

/** Configuration knobs for [ProactiveBriefingService]. Mirrors the C# options record. */
data class ProactiveBriefingOptions(
    /** UTC times-of-day at which to fire. Default: 06:30 and 18:00. */
    val fireTimesUtc: List<LocalTime> = listOf(LocalTime.of(6, 30), LocalTime.of(18, 0)),
    /** Latitude for weather lookup. Null = skip weather. */
    val latitude: Double? = null,
    /** Longitude for weather lookup. Null = skip weather. */
    val longitude: Double? = null,
    /** Headline used by the notifier. Default "Your briefing". */
    val headline: String = "Your briefing",
    /** Where to deliver the briefing (E.164 for SMS/WhatsApp, channel id for Telegram, …). */
    val deliveryAddress: String? = null,
)

// ---------------------------------------------------------------------------
// ProactiveBriefingService
// ---------------------------------------------------------------------------

/**
 * Assembles, summarises, and delivers a scheduled briefing. [start]/[stop]
 * manage the background fire loop; [fireOnceAsync] assembles the briefing on
 * demand (and is what tests drive). All connectors are optional — with none
 * configured the fire is a no-op.
 */
class ProactiveBriefingService(
    private val opts: ProactiveBriefingOptions,
    private val calendars: List<ICalendarConnector> = emptyList(),
    private val emails: List<IEmailConnector> = emptyList(),
    private val news: List<INewsSource> = emptyList(),
    private val weather: IWeatherProvider? = null,
    private val notifiers: List<IBriefingNotifier> = emptyList(),
    private val ai: IChatGenerator? = null,
) {
    private var scope: CoroutineScope? = null
    private var loop: Job? = null

    /** Start the background fire loop (idempotent). */
    fun start() {
        if (scope != null) return
        val s = CoroutineScope(Dispatchers.Default + Job())
        scope = s
        loop = s.launch { loopAsync() }
    }

    /** Stop the background fire loop and wait for it to unwind. */
    suspend fun stop() {
        val s = scope ?: return
        loop?.cancelAndJoin()
        s.coroutineContext[Job]?.cancel()
        scope = null
        loop = null
    }

    private suspend fun loopAsync() {
        val self = scope ?: return
        while (self.isActive) {
            val sleep = timeUntilNextFire(Instant.now())
            try {
                delay(sleep.toMillis())
            } catch (_: Exception) {
                return
            }
            try {
                fireOnceAsync()
            } catch (ex: Exception) {
                System.err.println("[ProactiveBriefingService] fire failed: ${ex.message}")
            }
        }
    }

    /**
     * Time until the next configured fire moment. Always > 30 s to avoid
     * double-fires — a candidate at or before now+30s rolls to the next day.
     * Ported faithfully from the C# reference.
     */
    internal fun timeUntilNextFire(now: Instant): Duration {
        if (opts.fireTimesUtc.isEmpty()) return Duration.ofHours(1)
        val todayBase = now.atZone(ZoneOffset.UTC).toLocalDate()
            .atStartOfDay().toInstant(ZoneOffset.UTC)
        var best: Duration? = null
        for (tod in opts.fireTimesUtc) {
            var candidate = todayBase.plus(Duration.ofNanos(tod.toNanoOfDay()))
            if (!candidate.isAfter(now.plusSeconds(30))) candidate = candidate.plus(Duration.ofDays(1))
            val gap = Duration.between(now, candidate)
            if (best == null || gap < best) best = gap
        }
        return best ?: Duration.ofHours(1)
    }

    /** Assemble the briefing context, summarise via the LLM, and deliver it. */
    suspend fun fireOnceAsync() {
        val ctxParts = ArrayList<String>()

        // Calendar — next 24 hours.
        for (cal in calendars.filter { it.isConfigured }) {
            try {
                val now = Instant.now()
                val events = cal.listEventsAsync(now, now.plus(Duration.ofHours(24)))
                if (events.isNotEmpty()) {
                    ctxParts.add("### Calendar (${cal.providerId})")
                    for (e in events.sortedBy { it.startUtc }.take(8)) {
                        val loc = if (e.location.isNullOrEmpty()) "" else " @ " + e.location
                        ctxParts.add("- ${localHhmm(e.startUtc)} ${e.title}$loc")
                    }
                }
            } catch (ex: Exception) {
                System.err.println("[briefing] calendar ${cal.providerId} skipped: ${ex.message}")
            }
        }

        // Email — unread.
        for (em in emails.filter { it.isConfigured }) {
            try {
                val unread = em.listUnreadAsync(5)
                if (unread.isNotEmpty()) {
                    ctxParts.add("### Unread email (${em.providerId})")
                    for (m in unread) ctxParts.add("- ${m.from}: ${m.subject}")
                }
            } catch (ex: Exception) {
                System.err.println("[briefing] email ${em.providerId} skipped: ${ex.message}")
            }
        }

        // News — latest from each source.
        for (src in news.filter { it.isConfigured }) {
            try {
                val items = src.fetchLatestAsync(5)
                if (items.isNotEmpty()) {
                    ctxParts.add("### News (${src.sourceId})")
                    for (i in items) ctxParts.add("- ${i.title}")
                }
            } catch (ex: Exception) {
                System.err.println("[briefing] news ${src.sourceId} skipped: ${ex.message}")
            }
        }

        // Weather — if location configured.
        val lat = opts.latitude
        val lon = opts.longitude
        if (weather != null && lat != null && lon != null) {
            try {
                val nowW = weather.currentAsync(lat, lon)
                ctxParts.add("### Weather (${weather.providerId})")
                ctxParts.add(
                    "- ${"%.0f".format(nowW.tempC)}°C ${nowW.condition}, " +
                        "feels ${"%.0f".format(nowW.feelsLikeC)}°C, wind ${"%.0f".format(nowW.windKph)} km/h",
                )
            } catch (ex: Exception) {
                System.err.println("[briefing] weather skipped: ${ex.message}")
            }
        }

        if (ctxParts.isEmpty()) return

        val context = ctxParts.joinToString("\n")
        val prompt = "Summarise the user's morning briefing in 80 words or less. Warm but factual. " +
            "End with the one thing they should do first today.\n\n" + context

        val summary: String = if (ai != null) {
            try {
                ai.generateAsync(listOf(ChatMessage(UUID.randomUUID().toString(), "user", prompt)))
            } catch (ex: Exception) {
                System.err.println("[briefing] AI summarisation failed; sending raw context: ${ex.message}")
                context
            }
        } else {
            context
        }

        for (notifier in notifiers) {
            try {
                notifier.deliverAsync(opts.headline, summary, opts.deliveryAddress)
            } catch (ex: Exception) {
                System.err.println("[briefing] notifier failed: ${ex.message}")
            }
        }
    }

    private companion object {
        /** Render an instant's local wall-clock HH:mm — mirrors C# `StartUtc.ToLocalTime():HH:mm`. */
        fun localHhmm(instant: Instant): String {
            val lt = instant.atZone(java.time.ZoneId.systemDefault())
            return "%02d:%02d".format(lt.hour, lt.minute)
        }
    }
}
