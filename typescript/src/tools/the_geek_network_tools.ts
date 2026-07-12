// tools/the_geek_network_tools.ts
//
// Static catalogue of tool definitions covering the 36 APIs in TheGeekNetwork
// ecosystem. Port of CircleAI.Tools.TheGeekNetworkTools. Tool names follow the
// pattern "tgn.<api_slug>.<verb>" in lowercase snake_case. Each API exposes 1-3
// representative operations rather than every endpoint.

import type { ToolDefinition, ToolParameter } from "./index.js";

/** Terse parameter constructor (C# `Param(type, description, enum?)`). */
function param(type: string, description: string, enumValues?: string[]): ToolParameter {
  return { type, description, enum: enumValues };
}

/**
 * Static catalogue of tool definitions for the TheGeekNetwork ecosystem. Mirrors
 * `CircleAI.Tools.TheGeekNetworkTools`.
 */
export const TheGeekNetworkTools = {
  // AccountAPI — user accounts
  account(): readonly ToolDefinition[] {
    return [
      {
        name: "tgn.account.get_profile",
        description:
          "Get the authenticated user's account profile (display name, email, phone, country, KYC level).",
        parameters: {
          user_id: param("string", "Target user ID. Use 'me' for the current authenticated user."),
        },
        requiredParameters: ["user_id"],
      },
      {
        name: "tgn.account.update_profile",
        description: "Update profile fields for the current user (display name, avatar, country).",
        parameters: {
          display_name: param("string", "New display name. Optional."),
          avatar_url: param("string", "URL of the new avatar image. Optional."),
          country_code: param("string", "ISO-3166 alpha-2 country code. Optional."),
        },
        requiredParameters: [],
      },
    ];
  },

  // AuditAPI — audit trail
  audit(): readonly ToolDefinition[] {
    return [
      {
        name: "tgn.audit.list_events",
        description:
          "List recent audit events for the authenticated user, optionally filtered by category.",
        parameters: {
          category: param("string", "Optional event category filter (e.g. 'auth', 'payment', 'profile')."),
          limit: param("number", "Max number of events to return. Default 50, max 500."),
        },
        requiredParameters: [],
      },
    ];
  },

  // AuthAPI — authentication / OTP / biometrics
  auth(): readonly ToolDefinition[] {
    return [
      {
        name: "tgn.auth.request_otp",
        description:
          "Send a one-time password to the user's phone via SMS for login or sensitive action confirmation.",
        parameters: {
          phone_number: param("string", "E.164-formatted phone number, e.g. +27821234567."),
          purpose: param("string", "Reason for the OTP.", ["login", "signup", "transaction", "reset_pin"]),
        },
        requiredParameters: ["phone_number", "purpose"],
      },
      {
        name: "tgn.auth.verify_otp",
        description: "Verify an OTP code previously sent to the user. Returns a session token on success.",
        parameters: {
          phone_number: param("string", "E.164-formatted phone number."),
          code: param("string", "The OTP code the user received."),
        },
        requiredParameters: ["phone_number", "code"],
      },
      {
        name: "tgn.auth.push_to_app",
        description:
          "Trigger a push-to-app biometric approval on the user's mobile device for a web login or sensitive action.",
        parameters: {
          session_id: param("string", "The web session awaiting approval."),
          reason: param("string", "Human-readable reason shown to the user on the device."),
        },
        requiredParameters: ["session_id", "reason"],
      },
    ];
  },

  // BidBaasAPI — auctions
  bidBaas(): readonly ToolDefinition[] {
    return [
      {
        name: "tgn.bidbaas.list_active_auctions",
        description:
          "List currently active BidBaas auctions, optionally filtered by category or location.",
        parameters: {
          category: param("string", "Optional category filter, e.g. 'electronics', 'vehicles'."),
          country_code: param("string", "Optional ISO-3166 country code."),
          limit: param("number", "Max number of auctions to return. Default 25."),
        },
        requiredParameters: [],
      },
      {
        name: "tgn.bidbaas.place_bid",
        description: "Place a bid on an active BidBaas auction.",
        parameters: {
          auction_id: param("string", "Auction identifier."),
          amount: param("number", "Bid amount in the auction's listed currency."),
          currency: param("string", "ISO-4217 currency code, e.g. 'ZAR', 'USD'."),
        },
        requiredParameters: ["auction_id", "amount", "currency"],
      },
      {
        name: "tgn.bidbaas.get_auction_details",
        description:
          "Get full details for a specific auction including current top bid, time remaining, and seller info.",
        parameters: {
          auction_id: param("string", "Auction identifier."),
        },
        requiredParameters: ["auction_id"],
      },
    ];
  },

  // BillPaymentAPI — utility/bill payments
  billPayment(): readonly ToolDefinition[] {
    return [
      {
        name: "tgn.billpayment.list_billers",
        description: "List available billers (utilities, telcos, councils) the user can pay.",
        parameters: {
          country_code: param("string", "ISO-3166 country code, e.g. 'ZA'."),
          category: param("string", "Optional category filter, e.g. 'water', 'rates', 'data'."),
        },
        requiredParameters: ["country_code"],
      },
      {
        name: "tgn.billpayment.pay_bill",
        description: "Pay a bill for a specified biller using the user's wallet balance.",
        parameters: {
          biller_id: param("string", "Biller identifier from list_billers."),
          account_number: param("string", "User's account number with that biller."),
          amount: param("number", "Amount to pay."),
          currency: param("string", "ISO-4217 currency code."),
        },
        requiredParameters: ["biller_id", "account_number", "amount", "currency"],
      },
    ];
  },

  // BlockchainAPI — Aether / SDPKT blockchain
  blockchain(): readonly ToolDefinition[] {
    return [
      {
        name: "tgn.blockchain.get_transaction",
        description: "Look up a SDPKT/Aether on-chain transaction by hash.",
        parameters: {
          tx_hash: param("string", "Transaction hash."),
        },
        requiredParameters: ["tx_hash"],
      },
      {
        name: "tgn.blockchain.get_address_info",
        description: "Get on-chain info about an Aether address (balance, recent activity).",
        parameters: {
          address: param("string", "Aether wallet address."),
        },
        requiredParameters: ["address"],
      },
    ];
  },

  // ButlerAPI — Butler/B! orchestration server-side
  butler(): readonly ToolDefinition[] {
    return [
      {
        name: "tgn.butler.log_interaction",
        description: "Log a B!/Butler interaction for analytics and personalisation.",
        parameters: {
          intent: param("string", "Detected intent name."),
          transcript: param("string", "Raw user utterance, redacted as needed."),
          success: param("boolean", "Whether the action succeeded."),
        },
        requiredParameters: ["intent", "transcript", "success"],
      },
      {
        name: "tgn.butler.get_user_context",
        description:
          "Fetch the server-side context for the current user (recent intents, preferences, capabilities).",
        parameters: {
          user_id: param("string", "Target user ID. Use 'me' for the current user."),
        },
        requiredParameters: ["user_id"],
      },
    ];
  },

  // CircleAetherAPI — mesh network
  circleAether(): readonly ToolDefinition[] {
    return [
      {
        name: "tgn.circleaether.get_node_status",
        description:
          "Get current mesh-node status (peers, throughput, region) for the authenticated device.",
        parameters: {
          device_id: param("string", "Device identifier. Use 'this' for the current device."),
        },
        requiredParameters: ["device_id"],
      },
      {
        name: "tgn.circleaether.list_nearby_peers",
        description:
          "List mesh peers reachable from the current node, with link quality and tipping eligibility.",
        parameters: {
          max_peers: param("number", "Max number of peers to return. Default 25."),
        },
        requiredParameters: [],
      },
    ];
  },

  // EcommerceAPI — generic ecommerce
  ecommerce(): readonly ToolDefinition[] {
    return [
      {
        name: "tgn.ecommerce.search_products",
        description: "Search the unified product catalogue across merchants in the ecosystem.",
        parameters: {
          query: param("string", "Free-text search query."),
          category: param("string", "Optional category filter."),
          max_price: param("number", "Optional maximum price."),
          currency: param("string", "ISO-4217 currency code."),
        },
        requiredParameters: ["query"],
      },
      {
        name: "tgn.ecommerce.get_product",
        description: "Get full product details by ID, including stock, variants, and merchant info.",
        parameters: {
          product_id: param("string", "Product identifier."),
        },
        requiredParameters: ["product_id"],
      },
    ];
  },

  // ElectricityAPI — prepaid electricity
  electricity(): readonly ToolDefinition[] {
    return [
      {
        name: "tgn.electricity.buy_token",
        description:
          "Buy prepaid electricity for a meter and return the STS token to enter into the meter.",
        parameters: {
          meter_number: param("string", "11-digit meter number."),
          amount: param("number", "Amount to spend on electricity."),
          currency: param("string", "ISO-4217 currency code, typically 'ZAR'."),
        },
        requiredParameters: ["meter_number", "amount", "currency"],
      },
      {
        name: "tgn.electricity.list_recent_purchases",
        description: "List the user's recent prepaid-electricity purchases.",
        parameters: {
          limit: param("number", "Max number of purchases to return. Default 10."),
        },
        requiredParameters: [],
      },
    ];
  },

  // GeoAPI — geocoding (address <-> coordinates)
  geo(): readonly ToolDefinition[] {
    return [
      {
        name: "tgn.geo.get_user_location",
        description:
          "Get the authenticated user's current best-known location (lat/lng, accuracy, country).",
        parameters: {},
        requiredParameters: [],
      },
      {
        name: "tgn.geo.geocode_address",
        description: "Convert a human-readable address to coordinates.",
        parameters: {
          address: param("string", "Free-text address to geocode."),
          country_code: param("string", "Optional ISO-3166 country bias."),
        },
        requiredParameters: ["address"],
      },
    ];
  },

  // GlocellAPI — Glocell retail trade
  glocell(): readonly ToolDefinition[] {
    return [
      {
        name: "tgn.glocell.list_products",
        description: "List Glocell retail products (airtime, data, vouchers) available to the user.",
        parameters: {
          category: param("string", "Optional category filter, e.g. 'airtime', 'data'."),
        },
        requiredParameters: [],
      },
    ];
  },

  // IncentivesAPI — gamification / Qi rewards
  incentives(): readonly ToolDefinition[] {
    return [
      {
        name: "tgn.incentives.get_qi_balance",
        description: "Get the user's current Qi (and Karma) balance and earning streak.",
        parameters: {},
        requiredParameters: [],
      },
      {
        name: "tgn.incentives.list_active_quests",
        description: "List quests/challenges the user can complete to earn Qi.",
        parameters: {
          limit: param("number", "Max number of quests to return. Default 10."),
        },
        requiredParameters: [],
      },
    ];
  },

  // KiffStoreAPI — KiffStore
  kiffStore(): readonly ToolDefinition[] {
    return [
      {
        name: "tgn.kiffstore.search_items",
        description: "Search KiffStore listings.",
        parameters: {
          query: param("string", "Free-text search query."),
          limit: param("number", "Max number of results. Default 25."),
        },
        requiredParameters: ["query"],
      },
    ];
  },

  // LedgerAPI — financial ledger
  ledger(): readonly ToolDefinition[] {
    return [
      {
        name: "tgn.ledger.get_account_balance",
        description: "Get the running balance for a ledger account belonging to the user.",
        parameters: {
          account_id: param("string", "Ledger account identifier."),
        },
        requiredParameters: ["account_id"],
      },
      {
        name: "tgn.ledger.list_entries",
        description: "List ledger entries for an account in reverse chronological order.",
        parameters: {
          account_id: param("string", "Ledger account identifier."),
          limit: param("number", "Max number of entries to return. Default 50."),
        },
        requiredParameters: ["account_id"],
      },
    ];
  },

  // LocalizationAPI — translations / 21 countries
  localization(): readonly ToolDefinition[] {
    return [
      {
        name: "tgn.localization.translate_text",
        description:
          "Translate a piece of text from one language to another using the ecosystem translation service.",
        parameters: {
          text: param("string", "Text to translate."),
          source_language: param("string", "ISO-639-1 source code or 'auto' for auto-detect."),
          target_language: param("string", "ISO-639-1 target code, e.g. 'en', 'zu', 'fr'."),
        },
        requiredParameters: ["text", "target_language"],
      },
      {
        name: "tgn.localization.list_supported_languages",
        description: "List all language codes supported by the ecosystem.",
        parameters: {},
        requiredParameters: [],
      },
    ];
  },

  // MapsAPI — DataAcuity maps (rendering / tiles / styles)
  maps(): readonly ToolDefinition[] {
    return [
      {
        name: "tgn.maps.geocode",
        description: "Forward-geocode an address to coordinates via DataAcuity.",
        parameters: {
          address: param("string", "Free-text address."),
        },
        requiredParameters: ["address"],
      },
      {
        name: "tgn.maps.reverse_geocode",
        description: "Reverse-geocode coordinates to an address.",
        parameters: {
          latitude: param("number", "Latitude in decimal degrees."),
          longitude: param("number", "Longitude in decimal degrees."),
        },
        requiredParameters: ["latitude", "longitude"],
      },
    ];
  },

  // MapsDataAPI — map data (POIs, routes, layers)
  mapsData(): readonly ToolDefinition[] {
    return [
      {
        name: "tgn.mapsdata.search_pois",
        description: "Search points of interest near a location, filtered by category.",
        parameters: {
          latitude: param("number", "Latitude in decimal degrees."),
          longitude: param("number", "Longitude in decimal degrees."),
          radius_meters: param("number", "Search radius in metres. Default 1000."),
          category: param("string", "Optional POI category, e.g. 'pharmacy', 'fuel'."),
        },
        requiredParameters: ["latitude", "longitude"],
      },
    ];
  },

  // MediaAPI — uploads / images
  media(): readonly ToolDefinition[] {
    return [
      {
        name: "tgn.media.create_upload_url",
        description:
          "Create a pre-signed URL the client can PUT a media file to. Does not upload the file itself.",
        parameters: {
          mime_type: param("string", "MIME type of the file, e.g. 'image/jpeg'."),
          size_bytes: param("number", "File size in bytes."),
        },
        requiredParameters: ["mime_type", "size_bytes"],
      },
      {
        name: "tgn.media.get_media",
        description: "Get metadata and a viewable URL for a previously uploaded media item.",
        parameters: {
          media_id: param("string", "Media identifier."),
        },
        requiredParameters: ["media_id"],
      },
    ];
  },

  // MessagingAPI — TxTMe messaging
  messaging(): readonly ToolDefinition[] {
    return [
      {
        name: "tgn.messaging.send_message",
        description: "Send a TxTMe message to a contact or conversation.",
        parameters: {
          recipient: param("string", "Recipient identifier - phone number (E.164) or user_id."),
          body: param("string", "Message body."),
          conversation_id: param("string", "Optional existing conversation to post into."),
        },
        requiredParameters: ["recipient", "body"],
      },
      {
        name: "tgn.messaging.list_conversations",
        description: "List the user's active TxTMe conversations, most recent first.",
        parameters: {
          limit: param("number", "Max number of conversations to return. Default 25."),
        },
        requiredParameters: [],
      },
      {
        name: "tgn.messaging.get_messages",
        description: "Get messages in a specific conversation, most recent first.",
        parameters: {
          conversation_id: param("string", "Conversation identifier."),
          limit: param("number", "Max number of messages to return. Default 50."),
        },
        requiredParameters: ["conversation_id"],
      },
    ];
  },

  // NotificationAPI — push notifications
  notification(): readonly ToolDefinition[] {
    return [
      {
        name: "tgn.notification.send_push",
        description: "Send a push notification to a user's registered devices.",
        parameters: {
          user_id: param("string", "Target user ID."),
          title: param("string", "Notification title."),
          body: param("string", "Notification body text."),
          data: param("object", "Optional structured payload for the app to handle."),
        },
        requiredParameters: ["user_id", "title", "body"],
      },
      {
        name: "tgn.notification.list_for_user",
        description: "List recent in-app notifications for the authenticated user.",
        parameters: {
          unread_only: param("boolean", "If true, return only unread notifications. Default false."),
          limit: param("number", "Max number to return. Default 50."),
        },
        requiredParameters: [],
      },
    ];
  },

  // OpSupportAPI — operations support
  opSupport(): readonly ToolDefinition[] {
    return [
      {
        name: "tgn.opsupport.create_ticket",
        description: "File a support ticket on the user's behalf.",
        parameters: {
          category: param("string", "Ticket category.", [
            "billing",
            "account",
            "bug",
            "feature_request",
            "other",
          ]),
          subject: param("string", "Short subject line."),
          body: param("string", "Full description of the issue."),
        },
        requiredParameters: ["category", "subject", "body"],
      },
      {
        name: "tgn.opsupport.get_system_status",
        description: "Get current system / API status (uptime, incidents).",
        parameters: {},
        requiredParameters: [],
      },
    ];
  },

  // PanikAPI — Panik SOS
  panik(): readonly ToolDefinition[] {
    return [
      {
        name: "tgn.panik.trigger_sos",
        description:
          "Trigger an SOS emergency alert. Notifies the user's panic contacts and optionally dispatches help.",
        parameters: {
          latitude: param("number", "Current latitude in decimal degrees."),
          longitude: param("number", "Current longitude in decimal degrees."),
          category: param("string", "Type of emergency.", ["medical", "crime", "fire", "accident", "other"]),
          note: param("string", "Optional short note describing the emergency."),
        },
        requiredParameters: ["latitude", "longitude", "category"],
      },
      {
        name: "tgn.panik.cancel_sos",
        description: "Cancel an in-progress SOS alert raised by the current user.",
        parameters: {
          alert_id: param("string", "SOS alert identifier."),
          reason: param("string", "Optional reason for cancellation."),
        },
        requiredParameters: ["alert_id"],
      },
    ];
  },

  // PayfastAPI — PayFast payments
  payfast(): readonly ToolDefinition[] {
    return [
      {
        name: "tgn.payfast.create_payment",
        description: "Create a PayFast payment intent and return the redirect URL the user should open.",
        parameters: {
          amount: param("number", "Amount to charge."),
          currency: param("string", "ISO-4217 currency code, e.g. 'ZAR'."),
          item_name: param("string", "Short description shown on the PayFast page."),
          return_url: param("string", "URL to return to on completion."),
        },
        requiredParameters: ["amount", "currency", "item_name"],
      },
    ];
  },

  // SdpktAPI — SDPKT wallet
  sdpkt(): readonly ToolDefinition[] {
    return [
      {
        name: "tgn.sdpkt.get_balance",
        description:
          "Get the user's SDPKT wallet balance, including any sub-balances (Qi, Karma, fiat-pegged).",
        parameters: {},
        requiredParameters: [],
      },
      {
        name: "tgn.sdpkt.send_payment",
        description: "Send an SDPKT payment to another user or wallet address.",
        parameters: {
          recipient: param("string", "Recipient identifier - user ID, phone number (E.164), or wallet address."),
          amount: param("number", "Amount to send."),
          currency: param("string", "Currency code: 'SDPKT', 'QI', 'KARMA', or fiat ISO-4217."),
          memo: param("string", "Optional memo attached to the transaction."),
        },
        requiredParameters: ["recipient", "amount", "currency"],
      },
      {
        name: "tgn.sdpkt.get_transactions",
        description: "List the user's recent SDPKT wallet transactions.",
        parameters: {
          limit: param("number", "Max number of transactions to return. Default 25."),
        },
        requiredParameters: [],
      },
    ];
  },

  // ShhMoneyAPI — discreet payments
  shhMoney(): readonly ToolDefinition[] {
    return [
      {
        name: "tgn.shhmoney.create_discreet_payment",
        description:
          "Create a discreet ShhMoney payment - sender and recipient identifiers are hidden from third parties on the ledger surface.",
        parameters: {
          recipient: param("string", "Recipient identifier."),
          amount: param("number", "Amount to send."),
          currency: param("string", "ISO-4217 currency code."),
        },
        requiredParameters: ["recipient", "amount", "currency"],
      },
    ];
  },

  // SleptOnAPI — SleptOn news/content
  sleptOn(): readonly ToolDefinition[] {
    return [
      {
        name: "tgn.slepton.list_stories",
        description: "List recent SleptOn stories, optionally filtered by topic or country.",
        parameters: {
          topic: param("string", "Optional topic filter."),
          country_code: param("string", "Optional ISO-3166 country code."),
          limit: param("number", "Max number of stories. Default 25."),
        },
        requiredParameters: [],
      },
      {
        name: "tgn.slepton.get_story",
        description: "Get a SleptOn story's full body and metadata.",
        parameters: {
          story_id: param("string", "Story identifier."),
        },
        requiredParameters: ["story_id"],
      },
    ];
  },

  // SortedClothingAPI — clothing
  sortedClothing(): readonly ToolDefinition[] {
    return [
      {
        name: "tgn.sortedclothing.search_items",
        description: "Search the SortedClothing inventory.",
        parameters: {
          query: param("string", "Free-text search query."),
          size: param("string", "Optional size filter."),
          limit: param("number", "Max results. Default 25."),
        },
        requiredParameters: ["query"],
      },
    ];
  },

  // TagMeAPI — TagMe geo-tagging
  tagMe(): readonly ToolDefinition[] {
    return [
      {
        name: "tgn.tagme.create_tag",
        description: "Create a geo-tag at a location with optional note and visibility.",
        parameters: {
          latitude: param("number", "Latitude in decimal degrees."),
          longitude: param("number", "Longitude in decimal degrees."),
          note: param("string", "Optional text note."),
          visibility: param("string", "Who can see the tag.", ["public", "friends", "private"]),
        },
        requiredParameters: ["latitude", "longitude"],
      },
      {
        name: "tgn.tagme.list_nearby_tags",
        description: "List geo-tags near a location.",
        parameters: {
          latitude: param("number", "Latitude in decimal degrees."),
          longitude: param("number", "Longitude in decimal degrees."),
          radius_meters: param("number", "Radius in metres. Default 500."),
        },
        requiredParameters: ["latitude", "longitude"],
      },
    ];
  },

  // TakemehomeAPI — travel comparison
  takemehome(): readonly ToolDefinition[] {
    return [
      {
        name: "tgn.takemehome.search_flights",
        description: "Search flights across multiple suppliers and return ranked options.",
        parameters: {
          origin: param("string", "Origin IATA code or city name."),
          destination: param("string", "Destination IATA code or city name."),
          depart_date: param("string", "Departure date in YYYY-MM-DD."),
          return_date: param("string", "Optional return date in YYYY-MM-DD."),
          passengers: param("number", "Number of passengers. Default 1."),
        },
        requiredParameters: ["origin", "destination", "depart_date"],
      },
      {
        name: "tgn.takemehome.search_stays",
        description: "Search accommodation options for a destination and date range.",
        parameters: {
          destination: param("string", "Destination city or area."),
          check_in: param("string", "Check-in date in YYYY-MM-DD."),
          check_out: param("string", "Check-out date in YYYY-MM-DD."),
          guests: param("number", "Number of guests. Default 1."),
        },
        requiredParameters: ["destination", "check_in", "check_out"],
      },
    ];
  },

  // TheHotListAPI — curated list
  theHotList(): readonly ToolDefinition[] {
    return [
      {
        name: "tgn.thehotlist.list_entries",
        description: "List curated 'hot list' entries, optionally filtered by category or country.",
        parameters: {
          category: param("string", "Optional category filter."),
          country_code: param("string", "Optional ISO-3166 country code."),
          limit: param("number", "Max entries to return. Default 25."),
        },
        requiredParameters: [],
      },
    ];
  },

  // TheJobCenterAPI — jobs
  theJobCenter(): readonly ToolDefinition[] {
    return [
      {
        name: "tgn.thejobcenter.search_jobs",
        description: "Search job postings.",
        parameters: {
          query: param("string", "Free-text search query, e.g. 'plumber Cape Town'."),
          country_code: param("string", "Optional ISO-3166 country code."),
          limit: param("number", "Max results. Default 25."),
        },
        requiredParameters: ["query"],
      },
      {
        name: "tgn.thejobcenter.apply",
        description: "Submit an application to a job posting on the user's behalf.",
        parameters: {
          job_id: param("string", "Job posting identifier."),
          cover_note: param("string", "Optional cover note."),
        },
        requiredParameters: ["job_id"],
      },
    ];
  },

  // ThirdPartyAPI — generic third-party integrations
  thirdParty(): readonly ToolDefinition[] {
    return [
      {
        name: "tgn.thirdparty.list_integrations",
        description:
          "List configured third-party integrations available to the user (e.g. Xero, Zapier-style hooks).",
        parameters: {},
        requiredParameters: [],
      },
      {
        name: "tgn.thirdparty.invoke_integration",
        description: "Invoke a registered third-party integration by name with a JSON payload.",
        parameters: {
          integration_name: param("string", "Integration name from list_integrations."),
          payload: param("object", "JSON payload to forward to the integration."),
        },
        requiredParameters: ["integration_name", "payload"],
      },
    ];
  },

  // TrustSealAPI — verification
  trustSeal(): readonly ToolDefinition[] {
    return [
      {
        name: "tgn.trustseal.get_status",
        description: "Get the user's TrustSeal verification status (KYC level, document checks).",
        parameters: {},
        requiredParameters: [],
      },
      {
        name: "tgn.trustseal.start_verification",
        description: "Start a verification flow for a specified KYC level.",
        parameters: {
          level: param("string", "Target KYC level.", ["basic", "verified", "enhanced"]),
        },
        requiredParameters: ["level"],
      },
    ];
  },

  // WalletAPI — generic wallet
  wallet(): readonly ToolDefinition[] {
    return [
      {
        name: "tgn.wallet.get_balance",
        description: "Get the user's wallet balance(s) across all supported currencies.",
        parameters: {
          currency: param("string", "Optional ISO-4217 currency to restrict the balance to."),
        },
        requiredParameters: [],
      },
      {
        name: "tgn.wallet.get_transactions",
        description: "List the user's recent wallet transactions.",
        parameters: {
          currency: param("string", "Optional ISO-4217 currency filter."),
          limit: param("number", "Max transactions to return. Default 25."),
        },
        requiredParameters: [],
      },
    ];
  },

  // WhatWeWantAPI — content stories
  whatWeWant(): readonly ToolDefinition[] {
    return [
      {
        name: "tgn.whatwewant.list_stories",
        description: "List WhatWeWant stories, sorted by recency.",
        parameters: {
          topic: param("string", "Optional topic filter."),
          limit: param("number", "Max stories to return. Default 25."),
        },
        requiredParameters: [],
      },
      {
        name: "tgn.whatwewant.get_story",
        description: "Get a single WhatWeWant story's full body and metadata.",
        parameters: {
          story_id: param("string", "Story identifier."),
        },
        requiredParameters: ["story_id"],
      },
    ];
  },

  // WolverineAPI — internal infra
  wolverine(): readonly ToolDefinition[] {
    return [
      {
        name: "tgn.wolverine.list_jobs",
        description: "List background jobs visible to the user (status, last run, next run).",
        parameters: {
          status: param("string", "Optional status filter.", ["queued", "running", "succeeded", "failed"]),
        },
        requiredParameters: [],
      },
    ];
  },

  /** Concatenate every API's tools into a single canonical list. */
  getAllTools(): readonly ToolDefinition[] {
    const all: ToolDefinition[] = [];
    all.push(...this.account());
    all.push(...this.audit());
    all.push(...this.auth());
    all.push(...this.bidBaas());
    all.push(...this.billPayment());
    all.push(...this.blockchain());
    all.push(...this.butler());
    all.push(...this.circleAether());
    all.push(...this.ecommerce());
    all.push(...this.electricity());
    all.push(...this.geo());
    all.push(...this.glocell());
    all.push(...this.incentives());
    all.push(...this.kiffStore());
    all.push(...this.ledger());
    all.push(...this.localization());
    all.push(...this.maps());
    all.push(...this.mapsData());
    all.push(...this.media());
    all.push(...this.messaging());
    all.push(...this.notification());
    all.push(...this.opSupport());
    all.push(...this.panik());
    all.push(...this.payfast());
    all.push(...this.sdpkt());
    all.push(...this.shhMoney());
    all.push(...this.sleptOn());
    all.push(...this.sortedClothing());
    all.push(...this.tagMe());
    all.push(...this.takemehome());
    all.push(...this.theHotList());
    all.push(...this.theJobCenter());
    all.push(...this.thirdParty());
    all.push(...this.trustSeal());
    all.push(...this.wallet());
    all.push(...this.whatWeWant());
    all.push(...this.wolverine());
    return all;
  },
};
