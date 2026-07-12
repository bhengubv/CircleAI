# the_geek_network_tools.py
#
# Port of CircleAI.Tools TheGeekNetworkTools.cs (C# — the EXACT spec).
#
# Static catalogue of tool definitions covering the 36 APIs in TheGeekNetwork
# ecosystem. Tool names follow the pattern "tgn.<api_slug>.<verb>" in lowercase
# snake_case. Each API exposes 1-3 representative operations rather than every
# endpoint.

from __future__ import annotations

from typing import List, Optional

from .tool_types import ToolDefinition, ToolParameter


def _p(type: str, description: str, enum: Optional[List[str]] = None) -> ToolParameter:
    """Terse ToolParameter construction — mirrors the C# ``Param`` helper."""
    return ToolParameter(type=type, description=description, enum=enum)


class TheGeekNetworkTools:
    """Static catalogue of the 36 TheGeekNetwork API tool definitions.
    Mirrors ``CircleAI.Tools.TheGeekNetworkTools``.
    """

    # ── AccountAPI — user accounts ─────────────────────────────────────────
    @staticmethod
    def account() -> List[ToolDefinition]:
        return [
            ToolDefinition(
                name="tgn.account.get_profile",
                description="Get the authenticated user's account profile (display name, email, phone, country, KYC level).",
                parameters={
                    "user_id": _p("string", "Target user ID. Use 'me' for the current authenticated user."),
                },
                required_parameters=["user_id"],
            ),
            ToolDefinition(
                name="tgn.account.update_profile",
                description="Update profile fields for the current user (display name, avatar, country).",
                parameters={
                    "display_name": _p("string", "New display name. Optional."),
                    "avatar_url": _p("string", "URL of the new avatar image. Optional."),
                    "country_code": _p("string", "ISO-3166 alpha-2 country code. Optional."),
                },
                required_parameters=[],
            ),
        ]

    # ── AuditAPI — audit trail ─────────────────────────────────────────────
    @staticmethod
    def audit() -> List[ToolDefinition]:
        return [
            ToolDefinition(
                name="tgn.audit.list_events",
                description="List recent audit events for the authenticated user, optionally filtered by category.",
                parameters={
                    "category": _p("string", "Optional event category filter (e.g. 'auth', 'payment', 'profile')."),
                    "limit": _p("number", "Max number of events to return. Default 50, max 500."),
                },
                required_parameters=[],
            ),
        ]

    # ── AuthAPI — authentication / OTP / biometrics ────────────────────────
    @staticmethod
    def auth() -> List[ToolDefinition]:
        return [
            ToolDefinition(
                name="tgn.auth.request_otp",
                description="Send a one-time password to the user's phone via SMS for login or sensitive action confirmation.",
                parameters={
                    "phone_number": _p("string", "E.164-formatted phone number, e.g. +27821234567."),
                    "purpose": _p("string", "Reason for the OTP.", ["login", "signup", "transaction", "reset_pin"]),
                },
                required_parameters=["phone_number", "purpose"],
            ),
            ToolDefinition(
                name="tgn.auth.verify_otp",
                description="Verify an OTP code previously sent to the user. Returns a session token on success.",
                parameters={
                    "phone_number": _p("string", "E.164-formatted phone number."),
                    "code": _p("string", "The OTP code the user received."),
                },
                required_parameters=["phone_number", "code"],
            ),
            ToolDefinition(
                name="tgn.auth.push_to_app",
                description="Trigger a push-to-app biometric approval on the user's mobile device for a web login or sensitive action.",
                parameters={
                    "session_id": _p("string", "The web session awaiting approval."),
                    "reason": _p("string", "Human-readable reason shown to the user on the device."),
                },
                required_parameters=["session_id", "reason"],
            ),
        ]

    # ── BidBaasAPI — auctions ──────────────────────────────────────────────
    @staticmethod
    def bid_baas() -> List[ToolDefinition]:
        return [
            ToolDefinition(
                name="tgn.bidbaas.list_active_auctions",
                description="List currently active BidBaas auctions, optionally filtered by category or location.",
                parameters={
                    "category": _p("string", "Optional category filter, e.g. 'electronics', 'vehicles'."),
                    "country_code": _p("string", "Optional ISO-3166 country code."),
                    "limit": _p("number", "Max number of auctions to return. Default 25."),
                },
                required_parameters=[],
            ),
            ToolDefinition(
                name="tgn.bidbaas.place_bid",
                description="Place a bid on an active BidBaas auction.",
                parameters={
                    "auction_id": _p("string", "Auction identifier."),
                    "amount": _p("number", "Bid amount in the auction's listed currency."),
                    "currency": _p("string", "ISO-4217 currency code, e.g. 'ZAR', 'USD'."),
                },
                required_parameters=["auction_id", "amount", "currency"],
            ),
            ToolDefinition(
                name="tgn.bidbaas.get_auction_details",
                description="Get full details for a specific auction including current top bid, time remaining, and seller info.",
                parameters={
                    "auction_id": _p("string", "Auction identifier."),
                },
                required_parameters=["auction_id"],
            ),
        ]

    # ── BillPaymentAPI — utility/bill payments ─────────────────────────────
    @staticmethod
    def bill_payment() -> List[ToolDefinition]:
        return [
            ToolDefinition(
                name="tgn.billpayment.list_billers",
                description="List available billers (utilities, telcos, councils) the user can pay.",
                parameters={
                    "country_code": _p("string", "ISO-3166 country code, e.g. 'ZA'."),
                    "category": _p("string", "Optional category filter, e.g. 'water', 'rates', 'data'."),
                },
                required_parameters=["country_code"],
            ),
            ToolDefinition(
                name="tgn.billpayment.pay_bill",
                description="Pay a bill for a specified biller using the user's wallet balance.",
                parameters={
                    "biller_id": _p("string", "Biller identifier from list_billers."),
                    "account_number": _p("string", "User's account number with that biller."),
                    "amount": _p("number", "Amount to pay."),
                    "currency": _p("string", "ISO-4217 currency code."),
                },
                required_parameters=["biller_id", "account_number", "amount", "currency"],
            ),
        ]

    # ── BlockchainAPI — Aether / SDPKT blockchain ──────────────────────────
    @staticmethod
    def blockchain() -> List[ToolDefinition]:
        return [
            ToolDefinition(
                name="tgn.blockchain.get_transaction",
                description="Look up a SDPKT/Aether on-chain transaction by hash.",
                parameters={
                    "tx_hash": _p("string", "Transaction hash."),
                },
                required_parameters=["tx_hash"],
            ),
            ToolDefinition(
                name="tgn.blockchain.get_address_info",
                description="Get on-chain info about an Aether address (balance, recent activity).",
                parameters={
                    "address": _p("string", "Aether wallet address."),
                },
                required_parameters=["address"],
            ),
        ]

    # ── ButlerAPI — Butler/B! orchestration server-side ────────────────────
    @staticmethod
    def butler() -> List[ToolDefinition]:
        return [
            ToolDefinition(
                name="tgn.butler.log_interaction",
                description="Log a B!/Butler interaction for analytics and personalisation.",
                parameters={
                    "intent": _p("string", "Detected intent name."),
                    "transcript": _p("string", "Raw user utterance, redacted as needed."),
                    "success": _p("boolean", "Whether the action succeeded."),
                },
                required_parameters=["intent", "transcript", "success"],
            ),
            ToolDefinition(
                name="tgn.butler.get_user_context",
                description="Fetch the server-side context for the current user (recent intents, preferences, capabilities).",
                parameters={
                    "user_id": _p("string", "Target user ID. Use 'me' for the current user."),
                },
                required_parameters=["user_id"],
            ),
        ]

    # ── CircleAetherAPI — mesh network ─────────────────────────────────────
    @staticmethod
    def circle_aether() -> List[ToolDefinition]:
        return [
            ToolDefinition(
                name="tgn.circleaether.get_node_status",
                description="Get current mesh-node status (peers, throughput, region) for the authenticated device.",
                parameters={
                    "device_id": _p("string", "Device identifier. Use 'this' for the current device."),
                },
                required_parameters=["device_id"],
            ),
            ToolDefinition(
                name="tgn.circleaether.list_nearby_peers",
                description="List mesh peers reachable from the current node, with link quality and tipping eligibility.",
                parameters={
                    "max_peers": _p("number", "Max number of peers to return. Default 25."),
                },
                required_parameters=[],
            ),
        ]

    # ── EcommerceAPI — generic ecommerce ───────────────────────────────────
    @staticmethod
    def ecommerce() -> List[ToolDefinition]:
        return [
            ToolDefinition(
                name="tgn.ecommerce.search_products",
                description="Search the unified product catalogue across merchants in the ecosystem.",
                parameters={
                    "query": _p("string", "Free-text search query."),
                    "category": _p("string", "Optional category filter."),
                    "max_price": _p("number", "Optional maximum price."),
                    "currency": _p("string", "ISO-4217 currency code."),
                },
                required_parameters=["query"],
            ),
            ToolDefinition(
                name="tgn.ecommerce.get_product",
                description="Get full product details by ID, including stock, variants, and merchant info.",
                parameters={
                    "product_id": _p("string", "Product identifier."),
                },
                required_parameters=["product_id"],
            ),
        ]

    # ── ElectricityAPI — prepaid electricity ───────────────────────────────
    @staticmethod
    def electricity() -> List[ToolDefinition]:
        return [
            ToolDefinition(
                name="tgn.electricity.buy_token",
                description="Buy prepaid electricity for a meter and return the STS token to enter into the meter.",
                parameters={
                    "meter_number": _p("string", "11-digit meter number."),
                    "amount": _p("number", "Amount to spend on electricity."),
                    "currency": _p("string", "ISO-4217 currency code, typically 'ZAR'."),
                },
                required_parameters=["meter_number", "amount", "currency"],
            ),
            ToolDefinition(
                name="tgn.electricity.list_recent_purchases",
                description="List the user's recent prepaid-electricity purchases.",
                parameters={
                    "limit": _p("number", "Max number of purchases to return. Default 10."),
                },
                required_parameters=[],
            ),
        ]

    # ── GeoAPI — geocoding (address <-> coordinates) ───────────────────────
    @staticmethod
    def geo() -> List[ToolDefinition]:
        return [
            ToolDefinition(
                name="tgn.geo.get_user_location",
                description="Get the authenticated user's current best-known location (lat/lng, accuracy, country).",
                parameters={},
                required_parameters=[],
            ),
            ToolDefinition(
                name="tgn.geo.geocode_address",
                description="Convert a human-readable address to coordinates.",
                parameters={
                    "address": _p("string", "Free-text address to geocode."),
                    "country_code": _p("string", "Optional ISO-3166 country bias."),
                },
                required_parameters=["address"],
            ),
        ]

    # ── GlocellAPI — Glocell retail trade ──────────────────────────────────
    @staticmethod
    def glocell() -> List[ToolDefinition]:
        return [
            ToolDefinition(
                name="tgn.glocell.list_products",
                description="List Glocell retail products (airtime, data, vouchers) available to the user.",
                parameters={
                    "category": _p("string", "Optional category filter, e.g. 'airtime', 'data'."),
                },
                required_parameters=[],
            ),
        ]

    # ── IncentivesAPI — gamification / Qi rewards ──────────────────────────
    @staticmethod
    def incentives() -> List[ToolDefinition]:
        return [
            ToolDefinition(
                name="tgn.incentives.get_qi_balance",
                description="Get the user's current Qi (and Karma) balance and earning streak.",
                parameters={},
                required_parameters=[],
            ),
            ToolDefinition(
                name="tgn.incentives.list_active_quests",
                description="List quests/challenges the user can complete to earn Qi.",
                parameters={
                    "limit": _p("number", "Max number of quests to return. Default 10."),
                },
                required_parameters=[],
            ),
        ]

    # ── KiffStoreAPI — KiffStore ───────────────────────────────────────────
    @staticmethod
    def kiff_store() -> List[ToolDefinition]:
        return [
            ToolDefinition(
                name="tgn.kiffstore.search_items",
                description="Search KiffStore listings.",
                parameters={
                    "query": _p("string", "Free-text search query."),
                    "limit": _p("number", "Max number of results. Default 25."),
                },
                required_parameters=["query"],
            ),
        ]

    # ── LedgerAPI — financial ledger ───────────────────────────────────────
    @staticmethod
    def ledger() -> List[ToolDefinition]:
        return [
            ToolDefinition(
                name="tgn.ledger.get_account_balance",
                description="Get the running balance for a ledger account belonging to the user.",
                parameters={
                    "account_id": _p("string", "Ledger account identifier."),
                },
                required_parameters=["account_id"],
            ),
            ToolDefinition(
                name="tgn.ledger.list_entries",
                description="List ledger entries for an account in reverse chronological order.",
                parameters={
                    "account_id": _p("string", "Ledger account identifier."),
                    "limit": _p("number", "Max number of entries to return. Default 50."),
                },
                required_parameters=["account_id"],
            ),
        ]

    # ── LocalizationAPI — translations / 21 countries ──────────────────────
    @staticmethod
    def localization() -> List[ToolDefinition]:
        return [
            ToolDefinition(
                name="tgn.localization.translate_text",
                description="Translate a piece of text from one language to another using the ecosystem translation service.",
                parameters={
                    "text": _p("string", "Text to translate."),
                    "source_language": _p("string", "ISO-639-1 source code or 'auto' for auto-detect."),
                    "target_language": _p("string", "ISO-639-1 target code, e.g. 'en', 'zu', 'fr'."),
                },
                required_parameters=["text", "target_language"],
            ),
            ToolDefinition(
                name="tgn.localization.list_supported_languages",
                description="List all language codes supported by the ecosystem.",
                parameters={},
                required_parameters=[],
            ),
        ]

    # ── MapsAPI — DataAcuity maps (rendering / tiles / styles) ─────────────
    @staticmethod
    def maps() -> List[ToolDefinition]:
        return [
            ToolDefinition(
                name="tgn.maps.geocode",
                description="Forward-geocode an address to coordinates via DataAcuity.",
                parameters={
                    "address": _p("string", "Free-text address."),
                },
                required_parameters=["address"],
            ),
            ToolDefinition(
                name="tgn.maps.reverse_geocode",
                description="Reverse-geocode coordinates to an address.",
                parameters={
                    "latitude": _p("number", "Latitude in decimal degrees."),
                    "longitude": _p("number", "Longitude in decimal degrees."),
                },
                required_parameters=["latitude", "longitude"],
            ),
        ]

    # ── MapsDataAPI — map data (POIs, routes, layers) ──────────────────────
    @staticmethod
    def maps_data() -> List[ToolDefinition]:
        return [
            ToolDefinition(
                name="tgn.mapsdata.search_pois",
                description="Search points of interest near a location, filtered by category.",
                parameters={
                    "latitude": _p("number", "Latitude in decimal degrees."),
                    "longitude": _p("number", "Longitude in decimal degrees."),
                    "radius_meters": _p("number", "Search radius in metres. Default 1000."),
                    "category": _p("string", "Optional POI category, e.g. 'pharmacy', 'fuel'."),
                },
                required_parameters=["latitude", "longitude"],
            ),
        ]

    # ── MediaAPI — uploads / images ────────────────────────────────────────
    @staticmethod
    def media() -> List[ToolDefinition]:
        return [
            ToolDefinition(
                name="tgn.media.create_upload_url",
                description="Create a pre-signed URL the client can PUT a media file to. Does not upload the file itself.",
                parameters={
                    "mime_type": _p("string", "MIME type of the file, e.g. 'image/jpeg'."),
                    "size_bytes": _p("number", "File size in bytes."),
                },
                required_parameters=["mime_type", "size_bytes"],
            ),
            ToolDefinition(
                name="tgn.media.get_media",
                description="Get metadata and a viewable URL for a previously uploaded media item.",
                parameters={
                    "media_id": _p("string", "Media identifier."),
                },
                required_parameters=["media_id"],
            ),
        ]

    # ── MessagingAPI — TxTMe messaging ─────────────────────────────────────
    @staticmethod
    def messaging() -> List[ToolDefinition]:
        return [
            ToolDefinition(
                name="tgn.messaging.send_message",
                description="Send a TxTMe message to a contact or conversation.",
                parameters={
                    "recipient": _p("string", "Recipient identifier - phone number (E.164) or user_id."),
                    "body": _p("string", "Message body."),
                    "conversation_id": _p("string", "Optional existing conversation to post into."),
                },
                required_parameters=["recipient", "body"],
            ),
            ToolDefinition(
                name="tgn.messaging.list_conversations",
                description="List the user's active TxTMe conversations, most recent first.",
                parameters={
                    "limit": _p("number", "Max number of conversations to return. Default 25."),
                },
                required_parameters=[],
            ),
            ToolDefinition(
                name="tgn.messaging.get_messages",
                description="Get messages in a specific conversation, most recent first.",
                parameters={
                    "conversation_id": _p("string", "Conversation identifier."),
                    "limit": _p("number", "Max number of messages to return. Default 50."),
                },
                required_parameters=["conversation_id"],
            ),
        ]

    # ── NotificationAPI — push notifications ───────────────────────────────
    @staticmethod
    def notification() -> List[ToolDefinition]:
        return [
            ToolDefinition(
                name="tgn.notification.send_push",
                description="Send a push notification to a user's registered devices.",
                parameters={
                    "user_id": _p("string", "Target user ID."),
                    "title": _p("string", "Notification title."),
                    "body": _p("string", "Notification body text."),
                    "data": _p("object", "Optional structured payload for the app to handle."),
                },
                required_parameters=["user_id", "title", "body"],
            ),
            ToolDefinition(
                name="tgn.notification.list_for_user",
                description="List recent in-app notifications for the authenticated user.",
                parameters={
                    "unread_only": _p("boolean", "If true, return only unread notifications. Default false."),
                    "limit": _p("number", "Max number to return. Default 50."),
                },
                required_parameters=[],
            ),
        ]

    # ── OpSupportAPI — operations support ──────────────────────────────────
    @staticmethod
    def op_support() -> List[ToolDefinition]:
        return [
            ToolDefinition(
                name="tgn.opsupport.create_ticket",
                description="File a support ticket on the user's behalf.",
                parameters={
                    "category": _p("string", "Ticket category.", ["billing", "account", "bug", "feature_request", "other"]),
                    "subject": _p("string", "Short subject line."),
                    "body": _p("string", "Full description of the issue."),
                },
                required_parameters=["category", "subject", "body"],
            ),
            ToolDefinition(
                name="tgn.opsupport.get_system_status",
                description="Get current system / API status (uptime, incidents).",
                parameters={},
                required_parameters=[],
            ),
        ]

    # ── PanikAPI — Panik SOS ───────────────────────────────────────────────
    @staticmethod
    def panik() -> List[ToolDefinition]:
        return [
            ToolDefinition(
                name="tgn.panik.trigger_sos",
                description="Trigger an SOS emergency alert. Notifies the user's panic contacts and optionally dispatches help.",
                parameters={
                    "latitude": _p("number", "Current latitude in decimal degrees."),
                    "longitude": _p("number", "Current longitude in decimal degrees."),
                    "category": _p("string", "Type of emergency.", ["medical", "crime", "fire", "accident", "other"]),
                    "note": _p("string", "Optional short note describing the emergency."),
                },
                required_parameters=["latitude", "longitude", "category"],
            ),
            ToolDefinition(
                name="tgn.panik.cancel_sos",
                description="Cancel an in-progress SOS alert raised by the current user.",
                parameters={
                    "alert_id": _p("string", "SOS alert identifier."),
                    "reason": _p("string", "Optional reason for cancellation."),
                },
                required_parameters=["alert_id"],
            ),
        ]

    # ── PayfastAPI — PayFast payments ──────────────────────────────────────
    @staticmethod
    def payfast() -> List[ToolDefinition]:
        return [
            ToolDefinition(
                name="tgn.payfast.create_payment",
                description="Create a PayFast payment intent and return the redirect URL the user should open.",
                parameters={
                    "amount": _p("number", "Amount to charge."),
                    "currency": _p("string", "ISO-4217 currency code, e.g. 'ZAR'."),
                    "item_name": _p("string", "Short description shown on the PayFast page."),
                    "return_url": _p("string", "URL to return to on completion."),
                },
                required_parameters=["amount", "currency", "item_name"],
            ),
        ]

    # ── SdpktAPI — SDPKT wallet ─────────────────────────────────────────────
    @staticmethod
    def sdpkt() -> List[ToolDefinition]:
        return [
            ToolDefinition(
                name="tgn.sdpkt.get_balance",
                description="Get the user's SDPKT wallet balance, including any sub-balances (Qi, Karma, fiat-pegged).",
                parameters={},
                required_parameters=[],
            ),
            ToolDefinition(
                name="tgn.sdpkt.send_payment",
                description="Send an SDPKT payment to another user or wallet address.",
                parameters={
                    "recipient": _p("string", "Recipient identifier - user ID, phone number (E.164), or wallet address."),
                    "amount": _p("number", "Amount to send."),
                    "currency": _p("string", "Currency code: 'SDPKT', 'QI', 'KARMA', or fiat ISO-4217."),
                    "memo": _p("string", "Optional memo attached to the transaction."),
                },
                required_parameters=["recipient", "amount", "currency"],
            ),
            ToolDefinition(
                name="tgn.sdpkt.get_transactions",
                description="List the user's recent SDPKT wallet transactions.",
                parameters={
                    "limit": _p("number", "Max number of transactions to return. Default 25."),
                },
                required_parameters=[],
            ),
        ]

    # ── ShhMoneyAPI — discreet payments ────────────────────────────────────
    @staticmethod
    def shh_money() -> List[ToolDefinition]:
        return [
            ToolDefinition(
                name="tgn.shhmoney.create_discreet_payment",
                description="Create a discreet ShhMoney payment - sender and recipient identifiers are hidden from third parties on the ledger surface.",
                parameters={
                    "recipient": _p("string", "Recipient identifier."),
                    "amount": _p("number", "Amount to send."),
                    "currency": _p("string", "ISO-4217 currency code."),
                },
                required_parameters=["recipient", "amount", "currency"],
            ),
        ]

    # ── SleptOnAPI — SleptOn news/content ──────────────────────────────────
    @staticmethod
    def slept_on() -> List[ToolDefinition]:
        return [
            ToolDefinition(
                name="tgn.slepton.list_stories",
                description="List recent SleptOn stories, optionally filtered by topic or country.",
                parameters={
                    "topic": _p("string", "Optional topic filter."),
                    "country_code": _p("string", "Optional ISO-3166 country code."),
                    "limit": _p("number", "Max number of stories. Default 25."),
                },
                required_parameters=[],
            ),
            ToolDefinition(
                name="tgn.slepton.get_story",
                description="Get a SleptOn story's full body and metadata.",
                parameters={
                    "story_id": _p("string", "Story identifier."),
                },
                required_parameters=["story_id"],
            ),
        ]

    # ── SortedClothingAPI — clothing ───────────────────────────────────────
    @staticmethod
    def sorted_clothing() -> List[ToolDefinition]:
        return [
            ToolDefinition(
                name="tgn.sortedclothing.search_items",
                description="Search the SortedClothing inventory.",
                parameters={
                    "query": _p("string", "Free-text search query."),
                    "size": _p("string", "Optional size filter."),
                    "limit": _p("number", "Max results. Default 25."),
                },
                required_parameters=["query"],
            ),
        ]

    # ── TagMeAPI — TagMe geo-tagging ───────────────────────────────────────
    @staticmethod
    def tag_me() -> List[ToolDefinition]:
        return [
            ToolDefinition(
                name="tgn.tagme.create_tag",
                description="Create a geo-tag at a location with optional note and visibility.",
                parameters={
                    "latitude": _p("number", "Latitude in decimal degrees."),
                    "longitude": _p("number", "Longitude in decimal degrees."),
                    "note": _p("string", "Optional text note."),
                    "visibility": _p("string", "Who can see the tag.", ["public", "friends", "private"]),
                },
                required_parameters=["latitude", "longitude"],
            ),
            ToolDefinition(
                name="tgn.tagme.list_nearby_tags",
                description="List geo-tags near a location.",
                parameters={
                    "latitude": _p("number", "Latitude in decimal degrees."),
                    "longitude": _p("number", "Longitude in decimal degrees."),
                    "radius_meters": _p("number", "Radius in metres. Default 500."),
                },
                required_parameters=["latitude", "longitude"],
            ),
        ]

    # ── TakemehomeAPI — travel comparison ──────────────────────────────────
    @staticmethod
    def takemehome() -> List[ToolDefinition]:
        return [
            ToolDefinition(
                name="tgn.takemehome.search_flights",
                description="Search flights across multiple suppliers and return ranked options.",
                parameters={
                    "origin": _p("string", "Origin IATA code or city name."),
                    "destination": _p("string", "Destination IATA code or city name."),
                    "depart_date": _p("string", "Departure date in YYYY-MM-DD."),
                    "return_date": _p("string", "Optional return date in YYYY-MM-DD."),
                    "passengers": _p("number", "Number of passengers. Default 1."),
                },
                required_parameters=["origin", "destination", "depart_date"],
            ),
            ToolDefinition(
                name="tgn.takemehome.search_stays",
                description="Search accommodation options for a destination and date range.",
                parameters={
                    "destination": _p("string", "Destination city or area."),
                    "check_in": _p("string", "Check-in date in YYYY-MM-DD."),
                    "check_out": _p("string", "Check-out date in YYYY-MM-DD."),
                    "guests": _p("number", "Number of guests. Default 1."),
                },
                required_parameters=["destination", "check_in", "check_out"],
            ),
        ]

    # ── TheHotListAPI — curated list ───────────────────────────────────────
    @staticmethod
    def the_hot_list() -> List[ToolDefinition]:
        return [
            ToolDefinition(
                name="tgn.thehotlist.list_entries",
                description="List curated 'hot list' entries, optionally filtered by category or country.",
                parameters={
                    "category": _p("string", "Optional category filter."),
                    "country_code": _p("string", "Optional ISO-3166 country code."),
                    "limit": _p("number", "Max entries to return. Default 25."),
                },
                required_parameters=[],
            ),
        ]

    # ── TheJobCenterAPI — jobs ─────────────────────────────────────────────
    @staticmethod
    def the_job_center() -> List[ToolDefinition]:
        return [
            ToolDefinition(
                name="tgn.thejobcenter.search_jobs",
                description="Search job postings.",
                parameters={
                    "query": _p("string", "Free-text search query, e.g. 'plumber Cape Town'."),
                    "country_code": _p("string", "Optional ISO-3166 country code."),
                    "limit": _p("number", "Max results. Default 25."),
                },
                required_parameters=["query"],
            ),
            ToolDefinition(
                name="tgn.thejobcenter.apply",
                description="Submit an application to a job posting on the user's behalf.",
                parameters={
                    "job_id": _p("string", "Job posting identifier."),
                    "cover_note": _p("string", "Optional cover note."),
                },
                required_parameters=["job_id"],
            ),
        ]

    # ── ThirdPartyAPI — generic third-party integrations ───────────────────
    @staticmethod
    def third_party() -> List[ToolDefinition]:
        return [
            ToolDefinition(
                name="tgn.thirdparty.list_integrations",
                description="List configured third-party integrations available to the user (e.g. Xero, Zapier-style hooks).",
                parameters={},
                required_parameters=[],
            ),
            ToolDefinition(
                name="tgn.thirdparty.invoke_integration",
                description="Invoke a registered third-party integration by name with a JSON payload.",
                parameters={
                    "integration_name": _p("string", "Integration name from list_integrations."),
                    "payload": _p("object", "JSON payload to forward to the integration."),
                },
                required_parameters=["integration_name", "payload"],
            ),
        ]

    # ── TrustSealAPI — verification ────────────────────────────────────────
    @staticmethod
    def trust_seal() -> List[ToolDefinition]:
        return [
            ToolDefinition(
                name="tgn.trustseal.get_status",
                description="Get the user's TrustSeal verification status (KYC level, document checks).",
                parameters={},
                required_parameters=[],
            ),
            ToolDefinition(
                name="tgn.trustseal.start_verification",
                description="Start a verification flow for a specified KYC level.",
                parameters={
                    "level": _p("string", "Target KYC level.", ["basic", "verified", "enhanced"]),
                },
                required_parameters=["level"],
            ),
        ]

    # ── WalletAPI — generic wallet ─────────────────────────────────────────
    @staticmethod
    def wallet() -> List[ToolDefinition]:
        return [
            ToolDefinition(
                name="tgn.wallet.get_balance",
                description="Get the user's wallet balance(s) across all supported currencies.",
                parameters={
                    "currency": _p("string", "Optional ISO-4217 currency to restrict the balance to."),
                },
                required_parameters=[],
            ),
            ToolDefinition(
                name="tgn.wallet.get_transactions",
                description="List the user's recent wallet transactions.",
                parameters={
                    "currency": _p("string", "Optional ISO-4217 currency filter."),
                    "limit": _p("number", "Max transactions to return. Default 25."),
                },
                required_parameters=[],
            ),
        ]

    # ── WhatWeWantAPI — content stories ────────────────────────────────────
    @staticmethod
    def what_we_want() -> List[ToolDefinition]:
        return [
            ToolDefinition(
                name="tgn.whatwewant.list_stories",
                description="List WhatWeWant stories, sorted by recency.",
                parameters={
                    "topic": _p("string", "Optional topic filter."),
                    "limit": _p("number", "Max stories to return. Default 25."),
                },
                required_parameters=[],
            ),
            ToolDefinition(
                name="tgn.whatwewant.get_story",
                description="Get a single WhatWeWant story's full body and metadata.",
                parameters={
                    "story_id": _p("string", "Story identifier."),
                },
                required_parameters=["story_id"],
            ),
        ]

    # ── WolverineAPI — internal infra ──────────────────────────────────────
    @staticmethod
    def wolverine() -> List[ToolDefinition]:
        return [
            ToolDefinition(
                name="tgn.wolverine.list_jobs",
                description="List background jobs visible to the user (status, last run, next run).",
                parameters={
                    "status": _p("string", "Optional status filter.", ["queued", "running", "succeeded", "failed"]),
                },
                required_parameters=[],
            ),
        ]

    # ── GetAllTools — concatenate every API's tools into one canonical list ─
    @staticmethod
    def get_all_tools() -> List[ToolDefinition]:
        """Concatenate every API's tools into a single canonical list.
        Mirrors ``TheGeekNetworkTools.GetAllTools`` (order-preserving).
        """
        all_tools: List[ToolDefinition] = []
        all_tools.extend(TheGeekNetworkTools.account())
        all_tools.extend(TheGeekNetworkTools.audit())
        all_tools.extend(TheGeekNetworkTools.auth())
        all_tools.extend(TheGeekNetworkTools.bid_baas())
        all_tools.extend(TheGeekNetworkTools.bill_payment())
        all_tools.extend(TheGeekNetworkTools.blockchain())
        all_tools.extend(TheGeekNetworkTools.butler())
        all_tools.extend(TheGeekNetworkTools.circle_aether())
        all_tools.extend(TheGeekNetworkTools.ecommerce())
        all_tools.extend(TheGeekNetworkTools.electricity())
        all_tools.extend(TheGeekNetworkTools.geo())
        all_tools.extend(TheGeekNetworkTools.glocell())
        all_tools.extend(TheGeekNetworkTools.incentives())
        all_tools.extend(TheGeekNetworkTools.kiff_store())
        all_tools.extend(TheGeekNetworkTools.ledger())
        all_tools.extend(TheGeekNetworkTools.localization())
        all_tools.extend(TheGeekNetworkTools.maps())
        all_tools.extend(TheGeekNetworkTools.maps_data())
        all_tools.extend(TheGeekNetworkTools.media())
        all_tools.extend(TheGeekNetworkTools.messaging())
        all_tools.extend(TheGeekNetworkTools.notification())
        all_tools.extend(TheGeekNetworkTools.op_support())
        all_tools.extend(TheGeekNetworkTools.panik())
        all_tools.extend(TheGeekNetworkTools.payfast())
        all_tools.extend(TheGeekNetworkTools.sdpkt())
        all_tools.extend(TheGeekNetworkTools.shh_money())
        all_tools.extend(TheGeekNetworkTools.slept_on())
        all_tools.extend(TheGeekNetworkTools.sorted_clothing())
        all_tools.extend(TheGeekNetworkTools.tag_me())
        all_tools.extend(TheGeekNetworkTools.takemehome())
        all_tools.extend(TheGeekNetworkTools.the_hot_list())
        all_tools.extend(TheGeekNetworkTools.the_job_center())
        all_tools.extend(TheGeekNetworkTools.third_party())
        all_tools.extend(TheGeekNetworkTools.trust_seal())
        all_tools.extend(TheGeekNetworkTools.wallet())
        all_tools.extend(TheGeekNetworkTools.what_we_want())
        all_tools.extend(TheGeekNetworkTools.wolverine())
        return all_tools
