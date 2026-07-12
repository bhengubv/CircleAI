// TheGeekNetworkTools.kt
//
// Kotlin port of CircleAI.Tools/TheGeekNetworkTools.cs.
//
// Static catalogue of tool definitions covering the 36 APIs in TheGeekNetwork
// ecosystem. Tool names follow "tgn.<api_slug>.<verb>" in lowercase snake_case.
// Each API exposes 1-3 representative operations rather than every endpoint.
//
// This is a data-only catalogue: [ToolDefinition]/[ToolParameter] live in
// Tools.kt. Registration/routing is the caller's job (see HttpToolBridge).

package com.bhengubv.circleai.tools

/**
 * Static catalogue of [ToolDefinition]s for TheGeekNetwork's 36 public APIs.
 * Grouped one function per API; [getAllTools] concatenates them into the
 * canonical list a bridge advertises.
 */
object TheGeekNetworkTools {

    /** Terse [ToolParameter] constructor mirroring the C# `Param` helper. */
    private fun param(type: String, description: String, enum: Array<String>? = null): ToolParameter =
        ToolParameter(type = type, description = description, enum = enum)

    fun account(): List<ToolDefinition> = listOf(
            ToolDefinition(
                "tgn.account.get_profile",
                "Get the authenticated user's account profile (display name, email, phone, country, KYC level).",
                mapOf(
                    "user_id" to param("string", "Target user ID. Use 'me' for the current authenticated user.")
                ),
                listOf( "user_id" )
            ),
            ToolDefinition(
                "tgn.account.update_profile",
                "Update profile fields for the current user (display name, avatar, country).",
                mapOf(
                    "display_name" to param("string", "New display name. Optional."),
                    "avatar_url" to param("string", "URL of the new avatar image. Optional."),
                    "country_code" to param("string", "ISO-3166 alpha-2 country code. Optional.")
                ),
                emptyList()
            )
        )
    // ============================================================================
    // AuditAPI — audit trail
    // ============================================================================

    fun audit(): List<ToolDefinition> = listOf(
            ToolDefinition(
                "tgn.audit.list_events",
                "List recent audit events for the authenticated user, optionally filtered by category.",
                mapOf(
                    "category" to param("string", "Optional event category filter (e.g. 'auth', 'payment', 'profile')."),
                    "limit" to param("number", "Max number of events to return. Default 50, max 500.")
                ),
                emptyList()
            )
        )
    // ============================================================================
    // AuthAPI — authentication / OTP / biometrics
    // ============================================================================

    fun auth(): List<ToolDefinition> = listOf(
            ToolDefinition(
                "tgn.auth.request_otp",
                "Send a one-time password to the user's phone via SMS for login or sensitive action confirmation.",
                mapOf(
                    "phone_number" to param("string", "E.164-formatted phone number, e.g. +27821234567."),
                    "purpose" to param("string", "Reason for the OTP.", arrayOf( "login", "signup", "transaction", "reset_pin" ))
                ),
                listOf( "phone_number", "purpose" )
            ),
            ToolDefinition(
                "tgn.auth.verify_otp",
                "Verify an OTP code previously sent to the user. Returns a session token on success.",
                mapOf(
                    "phone_number" to param("string", "E.164-formatted phone number."),
                    "code" to param("string", "The OTP code the user received.")
                ),
                listOf( "phone_number", "code" )
            ),
            ToolDefinition(
                "tgn.auth.push_to_app",
                "Trigger a push-to-app biometric approval on the user's mobile device for a web login or sensitive action.",
                mapOf(
                    "session_id" to param("string", "The web session awaiting approval."),
                    "reason" to param("string", "Human-readable reason shown to the user on the device.")
                ),
                listOf( "session_id", "reason" )
            )
        )
    // ============================================================================
    // BidBaasAPI — auctions
    // ============================================================================

    fun bidBaas(): List<ToolDefinition> = listOf(
            ToolDefinition(
                "tgn.bidbaas.list_active_auctions",
                "List currently active BidBaas auctions, optionally filtered by category or location.",
                mapOf(
                    "category" to param("string", "Optional category filter, e.g. 'electronics', 'vehicles'."),
                    "country_code" to param("string", "Optional ISO-3166 country code."),
                    "limit" to param("number", "Max number of auctions to return. Default 25.")
                ),
                emptyList()
            ),
            ToolDefinition(
                "tgn.bidbaas.place_bid",
                "Place a bid on an active BidBaas auction.",
                mapOf(
                    "auction_id" to param("string", "Auction identifier."),
                    "amount" to param("number", "Bid amount in the auction's listed currency."),
                    "currency" to param("string", "ISO-4217 currency code, e.g. 'ZAR', 'USD'.")
                ),
                listOf( "auction_id", "amount", "currency" )
            ),
            ToolDefinition(
                "tgn.bidbaas.get_auction_details",
                "Get full details for a specific auction including current top bid, time remaining, and seller info.",
                mapOf(
                    "auction_id" to param("string", "Auction identifier.")
                ),
                listOf( "auction_id" )
            )
        )
    // ============================================================================
    // BillPaymentAPI — utility/bill payments
    // ============================================================================

    fun billPayment(): List<ToolDefinition> = listOf(
            ToolDefinition(
                "tgn.billpayment.list_billers",
                "List available billers (utilities, telcos, councils) the user can pay.",
                mapOf(
                    "country_code" to param("string", "ISO-3166 country code, e.g. 'ZA'."),
                    "category" to param("string", "Optional category filter, e.g. 'water', 'rates', 'data'.")
                ),
                listOf( "country_code" )
            ),
            ToolDefinition(
                "tgn.billpayment.pay_bill",
                "Pay a bill for a specified biller using the user's wallet balance.",
                mapOf(
                    "biller_id" to param("string", "Biller identifier from list_billers."),
                    "account_number" to param("string", "User's account number with that biller."),
                    "amount" to param("number", "Amount to pay."),
                    "currency" to param("string", "ISO-4217 currency code.")
                ),
                listOf( "biller_id", "account_number", "amount", "currency" )
            )
        )
    // ============================================================================
    // BlockchainAPI — Aether / SDPKT blockchain
    // ============================================================================

    fun blockchain(): List<ToolDefinition> = listOf(
            ToolDefinition(
                "tgn.blockchain.get_transaction",
                "Look up a SDPKT/Aether on-chain transaction by hash.",
                mapOf(
                    "tx_hash" to param("string", "Transaction hash.")
                ),
                listOf( "tx_hash" )
            ),
            ToolDefinition(
                "tgn.blockchain.get_address_info",
                "Get on-chain info about an Aether address (balance, recent activity).",
                mapOf(
                    "address" to param("string", "Aether wallet address.")
                ),
                listOf( "address" )
            )
        )
    // ============================================================================
    // ButlerAPI — Butler/B! orchestration server-side
    // ============================================================================

    fun butler(): List<ToolDefinition> = listOf(
            ToolDefinition(
                "tgn.butler.log_interaction",
                "Log a B!/Butler interaction for analytics and personalisation.",
                mapOf(
                    "intent" to param("string", "Detected intent name."),
                    "transcript" to param("string", "Raw user utterance, redacted as needed."),
                    "success" to param("boolean", "Whether the action succeeded.")
                ),
                listOf( "intent", "transcript", "success" )
            ),
            ToolDefinition(
                "tgn.butler.get_user_context",
                "Fetch the server-side context for the current user (recent intents, preferences, capabilities).",
                mapOf(
                    "user_id" to param("string", "Target user ID. Use 'me' for the current user.")
                ),
                listOf( "user_id" )
            )
        )
    // ============================================================================
    // CircleAetherAPI — mesh network
    // ============================================================================

    fun circleAether(): List<ToolDefinition> = listOf(
            ToolDefinition(
                "tgn.circleaether.get_node_status",
                "Get current mesh-node status (peers, throughput, region) for the authenticated device.",
                mapOf(
                    "device_id" to param("string", "Device identifier. Use 'this' for the current device.")
                ),
                listOf( "device_id" )
            ),
            ToolDefinition(
                "tgn.circleaether.list_nearby_peers",
                "List mesh peers reachable from the current node, with link quality and tipping eligibility.",
                mapOf(
                    "max_peers" to param("number", "Max number of peers to return. Default 25.")
                ),
                emptyList()
            )
        )
    // ============================================================================
    // EcommerceAPI — generic ecommerce
    // ============================================================================

    fun ecommerce(): List<ToolDefinition> = listOf(
            ToolDefinition(
                "tgn.ecommerce.search_products",
                "Search the unified product catalogue across merchants in the ecosystem.",
                mapOf(
                    "query" to param("string", "Free-text search query."),
                    "category" to param("string", "Optional category filter."),
                    "max_price" to param("number", "Optional maximum price."),
                    "currency" to param("string", "ISO-4217 currency code.")
                ),
                listOf( "query" )
            ),
            ToolDefinition(
                "tgn.ecommerce.get_product",
                "Get full product details by ID, including stock, variants, and merchant info.",
                mapOf(
                    "product_id" to param("string", "Product identifier.")
                ),
                listOf( "product_id" )
            )
        )
    // ============================================================================
    // ElectricityAPI — prepaid electricity
    // ============================================================================

    fun electricity(): List<ToolDefinition> = listOf(
            ToolDefinition(
                "tgn.electricity.buy_token",
                "Buy prepaid electricity for a meter and return the STS token to enter into the meter.",
                mapOf(
                    "meter_number" to param("string", "11-digit meter number."),
                    "amount" to param("number", "Amount to spend on electricity."),
                    "currency" to param("string", "ISO-4217 currency code, typically 'ZAR'.")
                ),
                listOf( "meter_number", "amount", "currency" )
            ),
            ToolDefinition(
                "tgn.electricity.list_recent_purchases",
                "List the user's recent prepaid-electricity purchases.",
                mapOf(
                    "limit" to param("number", "Max number of purchases to return. Default 10.")
                ),
                emptyList()
            )
        )
    // ============================================================================
    // GeoAPI — geocoding (address <-> coordinates)
    // ============================================================================

    fun geo(): List<ToolDefinition> = listOf(
            ToolDefinition(
                "tgn.geo.get_user_location",
                "Get the authenticated user's current best-known location (lat/lng, accuracy, country).",
                mapOf<String, ToolParameter>(),
                emptyList()
            ),
            ToolDefinition(
                "tgn.geo.geocode_address",
                "Convert a human-readable address to coordinates.",
                mapOf(
                    "address" to param("string", "Free-text address to geocode."),
                    "country_code" to param("string", "Optional ISO-3166 country bias.")
                ),
                listOf( "address" )
            )
        )
    // ============================================================================
    // GlocellAPI — Glocell retail trade
    // ============================================================================

    fun glocell(): List<ToolDefinition> = listOf(
            ToolDefinition(
                "tgn.glocell.list_products",
                "List Glocell retail products (airtime, data, vouchers) available to the user.",
                mapOf(
                    "category" to param("string", "Optional category filter, e.g. 'airtime', 'data'.")
                ),
                emptyList()
            )
        )
    // ============================================================================
    // IncentivesAPI — gamification / Qi rewards
    // ============================================================================

    fun incentives(): List<ToolDefinition> = listOf(
            ToolDefinition(
                "tgn.incentives.get_qi_balance",
                "Get the user's current Qi (and Karma) balance and earning streak.",
                mapOf<String, ToolParameter>(),
                emptyList()
            ),
            ToolDefinition(
                "tgn.incentives.list_active_quests",
                "List quests/challenges the user can complete to earn Qi.",
                mapOf(
                    "limit" to param("number", "Max number of quests to return. Default 10.")
                ),
                emptyList()
            )
        )
    // ============================================================================
    // KiffStoreAPI — KiffStore
    // ============================================================================

    fun kiffStore(): List<ToolDefinition> = listOf(
            ToolDefinition(
                "tgn.kiffstore.search_items",
                "Search KiffStore listings.",
                mapOf(
                    "query" to param("string", "Free-text search query."),
                    "limit" to param("number", "Max number of results. Default 25.")
                ),
                listOf( "query" )
            )
        )
    // ============================================================================
    // LedgerAPI — financial ledger
    // ============================================================================

    fun ledger(): List<ToolDefinition> = listOf(
            ToolDefinition(
                "tgn.ledger.get_account_balance",
                "Get the running balance for a ledger account belonging to the user.",
                mapOf(
                    "account_id" to param("string", "Ledger account identifier.")
                ),
                listOf( "account_id" )
            ),
            ToolDefinition(
                "tgn.ledger.list_entries",
                "List ledger entries for an account in reverse chronological order.",
                mapOf(
                    "account_id" to param("string", "Ledger account identifier."),
                    "limit" to param("number", "Max number of entries to return. Default 50.")
                ),
                listOf( "account_id" )
            )
        )
    // ============================================================================
    // LocalizationAPI — translations / 21 countries
    // ============================================================================

    fun localization(): List<ToolDefinition> = listOf(
            ToolDefinition(
                "tgn.localization.translate_text",
                "Translate a piece of text from one language to another using the ecosystem translation service.",
                mapOf(
                    "text" to param("string", "Text to translate."),
                    "source_language" to param("string", "ISO-639-1 source code or 'auto' for auto-detect."),
                    "target_language" to param("string", "ISO-639-1 target code, e.g. 'en', 'zu', 'fr'.")
                ),
                listOf( "text", "target_language" )
            ),
            ToolDefinition(
                "tgn.localization.list_supported_languages",
                "List all language codes supported by the ecosystem.",
                mapOf<String, ToolParameter>(),
                emptyList()
            )
        )
    // ============================================================================
    // MapsAPI — DataAcuity maps (rendering / tiles / styles)
    // ============================================================================

    fun maps(): List<ToolDefinition> = listOf(
            ToolDefinition(
                "tgn.maps.geocode",
                "Forward-geocode an address to coordinates via DataAcuity.",
                mapOf(
                    "address" to param("string", "Free-text address.")
                ),
                listOf( "address" )
            ),
            ToolDefinition(
                "tgn.maps.reverse_geocode",
                "Reverse-geocode coordinates to an address.",
                mapOf(
                    "latitude" to param("number", "Latitude in decimal degrees."),
                    "longitude" to param("number", "Longitude in decimal degrees.")
                ),
                listOf( "latitude", "longitude" )
            )
        )
    // ============================================================================
    // MapsDataAPI — map data (POIs, routes, layers)
    // ============================================================================

    fun mapsData(): List<ToolDefinition> = listOf(
            ToolDefinition(
                "tgn.mapsdata.search_pois",
                "Search points of interest near a location, filtered by category.",
                mapOf(
                    "latitude" to param("number", "Latitude in decimal degrees."),
                    "longitude" to param("number", "Longitude in decimal degrees."),
                    "radius_meters" to param("number", "Search radius in metres. Default 1000."),
                    "category" to param("string", "Optional POI category, e.g. 'pharmacy', 'fuel'.")
                ),
                listOf( "latitude", "longitude" )
            )
        )
    // ============================================================================
    // MediaAPI — uploads / images
    // ============================================================================

    fun media(): List<ToolDefinition> = listOf(
            ToolDefinition(
                "tgn.media.create_upload_url",
                "Create a pre-signed URL the client can PUT a media file to. Does not upload the file itself.",
                mapOf(
                    "mime_type" to param("string", "MIME type of the file, e.g. 'image/jpeg'."),
                    "size_bytes" to param("number", "File size in bytes.")
                ),
                listOf( "mime_type", "size_bytes" )
            ),
            ToolDefinition(
                "tgn.media.get_media",
                "Get metadata and a viewable URL for a previously uploaded media item.",
                mapOf(
                    "media_id" to param("string", "Media identifier.")
                ),
                listOf( "media_id" )
            )
        )
    // ============================================================================
    // MessagingAPI — TxTMe messaging
    // ============================================================================

    fun messaging(): List<ToolDefinition> = listOf(
            ToolDefinition(
                "tgn.messaging.send_message",
                "Send a TxTMe message to a contact or conversation.",
                mapOf(
                    "recipient" to param("string", "Recipient identifier - phone number (E.164) or user_id."),
                    "body" to param("string", "Message body."),
                    "conversation_id" to param("string", "Optional existing conversation to post into.")
                ),
                listOf( "recipient", "body" )
            ),
            ToolDefinition(
                "tgn.messaging.list_conversations",
                "List the user's active TxTMe conversations, most recent first.",
                mapOf(
                    "limit" to param("number", "Max number of conversations to return. Default 25.")
                ),
                emptyList()
            ),
            ToolDefinition(
                "tgn.messaging.get_messages",
                "Get messages in a specific conversation, most recent first.",
                mapOf(
                    "conversation_id" to param("string", "Conversation identifier."),
                    "limit" to param("number", "Max number of messages to return. Default 50.")
                ),
                listOf( "conversation_id" )
            )
        )
    // ============================================================================
    // NotificationAPI — push notifications
    // ============================================================================

    fun notification(): List<ToolDefinition> = listOf(
            ToolDefinition(
                "tgn.notification.send_push",
                "Send a push notification to a user's registered devices.",
                mapOf(
                    "user_id" to param("string", "Target user ID."),
                    "title" to param("string", "Notification title."),
                    "body" to param("string", "Notification body text."),
                    "data" to param("object", "Optional structured payload for the app to handle.")
                ),
                listOf( "user_id", "title", "body" )
            ),
            ToolDefinition(
                "tgn.notification.list_for_user",
                "List recent in-app notifications for the authenticated user.",
                mapOf(
                    "unread_only" to param("boolean", "If true, return only unread notifications. Default false."),
                    "limit" to param("number", "Max number to return. Default 50.")
                ),
                emptyList()
            )
        )
    // ============================================================================
    // OpSupportAPI — operations support
    // ============================================================================

    fun opSupport(): List<ToolDefinition> = listOf(
            ToolDefinition(
                "tgn.opsupport.create_ticket",
                "File a support ticket on the user's behalf.",
                mapOf(
                    "category" to param("string", "Ticket category.", arrayOf( "billing", "account", "bug", "feature_request", "other" )),
                    "subject" to param("string", "Short subject line."),
                    "body" to param("string", "Full description of the issue.")
                ),
                listOf( "category", "subject", "body" )
            ),
            ToolDefinition(
                "tgn.opsupport.get_system_status",
                "Get current system / API status (uptime, incidents).",
                mapOf<String, ToolParameter>(),
                emptyList()
            )
        )
    // ============================================================================
    // PanikAPI — Panik SOS
    // ============================================================================

    fun panik(): List<ToolDefinition> = listOf(
            ToolDefinition(
                "tgn.panik.trigger_sos",
                "Trigger an SOS emergency alert. Notifies the user's panic contacts and optionally dispatches help.",
                mapOf(
                    "latitude" to param("number", "Current latitude in decimal degrees."),
                    "longitude" to param("number", "Current longitude in decimal degrees."),
                    "category" to param("string", "Type of emergency.", arrayOf( "medical", "crime", "fire", "accident", "other" )),
                    "note" to param("string", "Optional short note describing the emergency.")
                ),
                listOf( "latitude", "longitude", "category" )
            ),
            ToolDefinition(
                "tgn.panik.cancel_sos",
                "Cancel an in-progress SOS alert raised by the current user.",
                mapOf(
                    "alert_id" to param("string", "SOS alert identifier."),
                    "reason" to param("string", "Optional reason for cancellation.")
                ),
                listOf( "alert_id" )
            )
        )
    // ============================================================================
    // PayfastAPI — PayFast payments
    // ============================================================================

    fun payfast(): List<ToolDefinition> = listOf(
            ToolDefinition(
                "tgn.payfast.create_payment",
                "Create a PayFast payment intent and return the redirect URL the user should open.",
                mapOf(
                    "amount" to param("number", "Amount to charge."),
                    "currency" to param("string", "ISO-4217 currency code, e.g. 'ZAR'."),
                    "item_name" to param("string", "Short description shown on the PayFast page."),
                    "return_url" to param("string", "URL to return to on completion.")
                ),
                listOf( "amount", "currency", "item_name" )
            )
        )
    // ============================================================================
    // SdpktAPI — SDPKT wallet
    // ============================================================================

    fun sdpkt(): List<ToolDefinition> = listOf(
            ToolDefinition(
                "tgn.sdpkt.get_balance",
                "Get the user's SDPKT wallet balance, including any sub-balances (Qi, Karma, fiat-pegged).",
                mapOf<String, ToolParameter>(),
                emptyList()
            ),
            ToolDefinition(
                "tgn.sdpkt.send_payment",
                "Send an SDPKT payment to another user or wallet address.",
                mapOf(
                    "recipient" to param("string", "Recipient identifier - user ID, phone number (E.164), or wallet address."),
                    "amount" to param("number", "Amount to send."),
                    "currency" to param("string", "Currency code: 'SDPKT', 'QI', 'KARMA', or fiat ISO-4217."),
                    "memo" to param("string", "Optional memo attached to the transaction.")
                ),
                listOf( "recipient", "amount", "currency" )
            ),
            ToolDefinition(
                "tgn.sdpkt.get_transactions",
                "List the user's recent SDPKT wallet transactions.",
                mapOf(
                    "limit" to param("number", "Max number of transactions to return. Default 25.")
                ),
                emptyList()
            )
        )
    // ============================================================================
    // ShhMoneyAPI — discreet payments
    // ============================================================================

    fun shhMoney(): List<ToolDefinition> = listOf(
            ToolDefinition(
                "tgn.shhmoney.create_discreet_payment",
                "Create a discreet ShhMoney payment - sender and recipient identifiers are hidden from third parties on the ledger surface.",
                mapOf(
                    "recipient" to param("string", "Recipient identifier."),
                    "amount" to param("number", "Amount to send."),
                    "currency" to param("string", "ISO-4217 currency code.")
                ),
                listOf( "recipient", "amount", "currency" )
            )
        )
    // ============================================================================
    // SleptOnAPI — SleptOn news/content
    // ============================================================================

    fun sleptOn(): List<ToolDefinition> = listOf(
            ToolDefinition(
                "tgn.slepton.list_stories",
                "List recent SleptOn stories, optionally filtered by topic or country.",
                mapOf(
                    "topic" to param("string", "Optional topic filter."),
                    "country_code" to param("string", "Optional ISO-3166 country code."),
                    "limit" to param("number", "Max number of stories. Default 25.")
                ),
                emptyList()
            ),
            ToolDefinition(
                "tgn.slepton.get_story",
                "Get a SleptOn story's full body and metadata.",
                mapOf(
                    "story_id" to param("string", "Story identifier.")
                ),
                listOf( "story_id" )
            )
        )
    // ============================================================================
    // SortedClothingAPI — clothing
    // ============================================================================

    fun sortedClothing(): List<ToolDefinition> = listOf(
            ToolDefinition(
                "tgn.sortedclothing.search_items",
                "Search the SortedClothing inventory.",
                mapOf(
                    "query" to param("string", "Free-text search query."),
                    "size" to param("string", "Optional size filter."),
                    "limit" to param("number", "Max results. Default 25.")
                ),
                listOf( "query" )
            )
        )
    // ============================================================================
    // TagMeAPI — TagMe geo-tagging
    // ============================================================================

    fun tagMe(): List<ToolDefinition> = listOf(
            ToolDefinition(
                "tgn.tagme.create_tag",
                "Create a geo-tag at a location with optional note and visibility.",
                mapOf(
                    "latitude" to param("number", "Latitude in decimal degrees."),
                    "longitude" to param("number", "Longitude in decimal degrees."),
                    "note" to param("string", "Optional text note."),
                    "visibility" to param("string", "Who can see the tag.", arrayOf( "public", "friends", "private" ))
                ),
                listOf( "latitude", "longitude" )
            ),
            ToolDefinition(
                "tgn.tagme.list_nearby_tags",
                "List geo-tags near a location.",
                mapOf(
                    "latitude" to param("number", "Latitude in decimal degrees."),
                    "longitude" to param("number", "Longitude in decimal degrees."),
                    "radius_meters" to param("number", "Radius in metres. Default 500.")
                ),
                listOf( "latitude", "longitude" )
            )
        )
    // ============================================================================
    // TakemehomeAPI — travel comparison
    // ============================================================================

    fun takemehome(): List<ToolDefinition> = listOf(
            ToolDefinition(
                "tgn.takemehome.search_flights",
                "Search flights across multiple suppliers and return ranked options.",
                mapOf(
                    "origin" to param("string", "Origin IATA code or city name."),
                    "destination" to param("string", "Destination IATA code or city name."),
                    "depart_date" to param("string", "Departure date in YYYY-MM-DD."),
                    "return_date" to param("string", "Optional return date in YYYY-MM-DD."),
                    "passengers" to param("number", "Number of passengers. Default 1.")
                ),
                listOf( "origin", "destination", "depart_date" )
            ),
            ToolDefinition(
                "tgn.takemehome.search_stays",
                "Search accommodation options for a destination and date range.",
                mapOf(
                    "destination" to param("string", "Destination city or area."),
                    "check_in" to param("string", "Check-in date in YYYY-MM-DD."),
                    "check_out" to param("string", "Check-out date in YYYY-MM-DD."),
                    "guests" to param("number", "Number of guests. Default 1.")
                ),
                listOf( "destination", "check_in", "check_out" )
            )
        )
    // ============================================================================
    // TheHotListAPI — curated list
    // ============================================================================

    fun theHotList(): List<ToolDefinition> = listOf(
            ToolDefinition(
                "tgn.thehotlist.list_entries",
                "List curated 'hot list' entries, optionally filtered by category or country.",
                mapOf(
                    "category" to param("string", "Optional category filter."),
                    "country_code" to param("string", "Optional ISO-3166 country code."),
                    "limit" to param("number", "Max entries to return. Default 25.")
                ),
                emptyList()
            )
        )
    // ============================================================================
    // TheJobCenterAPI — jobs
    // ============================================================================

    fun theJobCenter(): List<ToolDefinition> = listOf(
            ToolDefinition(
                "tgn.thejobcenter.search_jobs",
                "Search job postings.",
                mapOf(
                    "query" to param("string", "Free-text search query, e.g. 'plumber Cape Town'."),
                    "country_code" to param("string", "Optional ISO-3166 country code."),
                    "limit" to param("number", "Max results. Default 25.")
                ),
                listOf( "query" )
            ),
            ToolDefinition(
                "tgn.thejobcenter.apply",
                "Submit an application to a job posting on the user's behalf.",
                mapOf(
                    "job_id" to param("string", "Job posting identifier."),
                    "cover_note" to param("string", "Optional cover note.")
                ),
                listOf( "job_id" )
            )
        )
    // ============================================================================
    // ThirdPartyAPI — generic third-party integrations
    // ============================================================================

    fun thirdParty(): List<ToolDefinition> = listOf(
            ToolDefinition(
                "tgn.thirdparty.list_integrations",
                "List configured third-party integrations available to the user (e.g. Xero, Zapier-style hooks).",
                mapOf<String, ToolParameter>(),
                emptyList()
            ),
            ToolDefinition(
                "tgn.thirdparty.invoke_integration",
                "Invoke a registered third-party integration by name with a JSON payload.",
                mapOf(
                    "integration_name" to param("string", "Integration name from list_integrations."),
                    "payload" to param("object", "JSON payload to forward to the integration.")
                ),
                listOf( "integration_name", "payload" )
            )
        )
    // ============================================================================
    // TrustSealAPI — verification
    // ============================================================================

    fun trustSeal(): List<ToolDefinition> = listOf(
            ToolDefinition(
                "tgn.trustseal.get_status",
                "Get the user's TrustSeal verification status (KYC level, document checks).",
                mapOf<String, ToolParameter>(),
                emptyList()
            ),
            ToolDefinition(
                "tgn.trustseal.start_verification",
                "Start a verification flow for a specified KYC level.",
                mapOf(
                    "level" to param("string", "Target KYC level.", arrayOf( "basic", "verified", "enhanced" ))
                ),
                listOf( "level" )
            )
        )
    // ============================================================================
    // WalletAPI — generic wallet
    // ============================================================================

    fun wallet(): List<ToolDefinition> = listOf(
            ToolDefinition(
                "tgn.wallet.get_balance",
                "Get the user's wallet balance(s) across all supported currencies.",
                mapOf(
                    "currency" to param("string", "Optional ISO-4217 currency to restrict the balance to.")
                ),
                emptyList()
            ),
            ToolDefinition(
                "tgn.wallet.get_transactions",
                "List the user's recent wallet transactions.",
                mapOf(
                    "currency" to param("string", "Optional ISO-4217 currency filter."),
                    "limit" to param("number", "Max transactions to return. Default 25.")
                ),
                emptyList()
            )
        )
    // ============================================================================
    // WhatWeWantAPI — content stories
    // ============================================================================

    fun whatWeWant(): List<ToolDefinition> = listOf(
            ToolDefinition(
                "tgn.whatwewant.list_stories",
                "List WhatWeWant stories, sorted by recency.",
                mapOf(
                    "topic" to param("string", "Optional topic filter."),
                    "limit" to param("number", "Max stories to return. Default 25.")
                ),
                emptyList()
            ),
            ToolDefinition(
                "tgn.whatwewant.get_story",
                "Get a single WhatWeWant story's full body and metadata.",
                mapOf(
                    "story_id" to param("string", "Story identifier.")
                ),
                listOf( "story_id" )
            )
        )
    // ============================================================================
    // WolverineAPI — internal infra
    // ============================================================================

    fun wolverine(): List<ToolDefinition> = listOf(
            ToolDefinition(
                "tgn.wolverine.list_jobs",
                "List background jobs visible to the user (status, last run, next run).",
                mapOf(
                    "status" to param("string", "Optional status filter.", arrayOf( "queued", "running", "succeeded", "failed" ))
                ),
                emptyList()
            )
        )
    // ============================================================================
    // GetAllTools — concatenate every API's tools into a single canonical list.
    // ============================================================================

    fun getAllTools(): List<ToolDefinition> = buildList {

        addAll(account())
        addAll(audit())
        addAll(auth())
        addAll(bidBaas())
        addAll(billPayment())
        addAll(blockchain())
        addAll(butler())
        addAll(circleAether())
        addAll(ecommerce())
        addAll(electricity())
        addAll(geo())
        addAll(glocell())
        addAll(incentives())
        addAll(kiffStore())
        addAll(ledger())
        addAll(localization())
        addAll(maps())
        addAll(mapsData())
        addAll(media())
        addAll(messaging())
        addAll(notification())
        addAll(opSupport())
        addAll(panik())
        addAll(payfast())
        addAll(sdpkt())
        addAll(shhMoney())
        addAll(sleptOn())
        addAll(sortedClothing())
        addAll(tagMe())
        addAll(takemehome())
        addAll(theHotList())
        addAll(theJobCenter())
        addAll(thirdParty())
        addAll(trustSeal())
        addAll(wallet())
        addAll(whatWeWant())
        addAll(wolverine())

    }
    }
