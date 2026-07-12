// tools/http_tool_bridge.ts
//
// HTTP-backed IToolBridge that routes tool calls to the TheGeekNetwork APIs over
// REST. Port of CircleAI.Tools.HttpToolBridge. Tool-name → endpoint mapping is
// provided for the representative operations defined in TheGeekNetworkTools;
// unmapped tools return a structured error rather than throwing.
//
// The .NET original constructs its own HttpClient; here the transport is
// injected behind the shared IHttpClient seam (integration/index.ts) so the
// bridge is deterministic and needs no real network — mirroring the connector
// convention. No network calls happen during construction or via availableTools;
// only invokeAsync hits the wire.
//
// JSON response bodies (integration HttpResponse.body is a string) are parsed
// with JSON.parse — the analogue of ReadFromJsonAsync<JsonElement>. Non-JSON
// bodies are surfaced as the raw string.

import {
  isSuccessStatusCode,
  resolveUrl,
  type HttpMethod,
  type HttpRequest,
  type HttpResponse,
  type IHttpClient,
} from "../integration/index.js";
import { TheGeekNetworkTools } from "./the_geek_network_tools.js";
import type { IToolBridge } from "./tool_bridge.js";
import { toolResultFailure, type ToolDefinition, type ToolInvocation, type ToolResult } from "./index.js";

// Body strategy values.
type BodyStrategy = "none" | "query" | "json";

interface EndpointMapping {
  readonly method: HttpMethod;
  readonly pathTemplate: string;
  readonly body: BodyStrategy;
}

// eslint-disable-next-line @typescript-eslint/no-explicit-any
type Args = Record<string, any>;

/**
 * HTTP-backed implementation of {@link IToolBridge}. Mirrors
 * `CircleAI.Tools.HttpToolBridge`.
 */
export class HttpToolBridge implements IToolBridge {
  private readonly http: IHttpClient;
  private readonly baseUrl: string;
  private readonly tools: readonly ToolDefinition[];
  private readonly routes: ReadonlyMap<string, EndpointMapping>;

  /**
   * @param baseUrl Absolute base URL of the TheGeekNetwork gateway.
   * @param httpClient Injected HTTP transport (the wire seam).
   * @param tools Tool catalogue to expose. Defaults to {@link TheGeekNetworkTools.getAllTools}.
   */
  constructor(baseUrl: string, httpClient: IHttpClient, tools?: readonly ToolDefinition[]) {
    if (baseUrl === null || baseUrl === undefined || baseUrl.trim().length === 0) {
      throw new Error("baseUrl must not be null or whitespace.");
    }
    if (httpClient === null || httpClient === undefined) throw new Error("httpClient is required.");

    this.http = httpClient;
    this.baseUrl = baseUrl.endsWith("/") ? baseUrl : baseUrl + "/";
    this.tools = tools ?? TheGeekNetworkTools.getAllTools();
    this.routes = HttpToolBridge.buildRoutes();
  }

  get availableTools(): readonly ToolDefinition[] {
    return this.tools;
  }

  getAvailableToolsAsync(_signal?: AbortSignal): Promise<readonly ToolDefinition[]> {
    return Promise.resolve(this.tools);
  }

  async invokeAsync(invocation: ToolInvocation, signal?: AbortSignal): Promise<ToolResult> {
    if (invocation === null || invocation === undefined) throw new Error("invocation is required.");

    const mapping = this.routes.get(invocation.toolName);
    if (mapping === undefined) {
      return {
        toolName: invocation.toolName,
        success: false,
        error: `Tool '${invocation.toolName}' is not registered in this bridge instance.`,
      };
    }

    try {
      const url = this.resolveUrl(mapping, invocation.arguments ?? {});
      const request = HttpToolBridge.buildRequest(mapping, url, invocation.arguments ?? {});
      const response: HttpResponse = await this.http.send(request, signal);

      const body = parseBody(response.body);

      if (!isSuccessStatusCode(response.statusCode)) {
        return {
          toolName: invocation.toolName,
          success: false,
          result: body,
          error: `HTTP ${response.statusCode}`,
        };
      }

      return { toolName: invocation.toolName, success: true, result: body };
    } catch (ex) {
      if (isAbort(ex, signal)) throw ex;
      return toolResultFailure(invocation.toolName, errorMessage(ex));
    }
  }

  // ── Routing table ──────────────────────────────────────────────────────────

  private static buildRoutes(): ReadonlyMap<string, EndpointMapping> {
    const m = new Map<string, EndpointMapping>();
    const add = (name: string, method: HttpMethod, pathTemplate: string, body: BodyStrategy) =>
      m.set(name, { method, pathTemplate, body });

    // Account
    add("tgn.account.get_profile", "GET", "account/v1/users/{user_id}", "none");
    add("tgn.account.update_profile", "PATCH", "account/v1/users/me", "json");
    // Audit
    add("tgn.audit.list_events", "GET", "audit/v1/events", "query");
    // Auth
    add("tgn.auth.request_otp", "POST", "auth/v1/otp/request", "json");
    add("tgn.auth.verify_otp", "POST", "auth/v1/otp/verify", "json");
    add("tgn.auth.push_to_app", "POST", "auth/v1/push-to-app", "json");
    // BidBaas
    add("tgn.bidbaas.list_active_auctions", "GET", "bidbaas/v1/auctions/active", "query");
    add("tgn.bidbaas.place_bid", "POST", "bidbaas/v1/auctions/{auction_id}/bids", "json");
    add("tgn.bidbaas.get_auction_details", "GET", "bidbaas/v1/auctions/{auction_id}", "none");
    // BillPayment
    add("tgn.billpayment.list_billers", "GET", "billpayment/v1/billers", "query");
    add("tgn.billpayment.pay_bill", "POST", "billpayment/v1/payments", "json");
    // Blockchain
    add("tgn.blockchain.get_transaction", "GET", "blockchain/v1/transactions/{tx_hash}", "none");
    add("tgn.blockchain.get_address_info", "GET", "blockchain/v1/addresses/{address}", "none");
    // Butler
    add("tgn.butler.log_interaction", "POST", "butler/v1/interactions", "json");
    add("tgn.butler.get_user_context", "GET", "butler/v1/users/{user_id}/context", "none");
    // CircleAether
    add("tgn.circleaether.get_node_status", "GET", "circleaether/v1/nodes/{device_id}/status", "none");
    add("tgn.circleaether.list_nearby_peers", "GET", "circleaether/v1/peers/nearby", "query");
    // Ecommerce
    add("tgn.ecommerce.search_products", "GET", "ecommerce/v1/products/search", "query");
    add("tgn.ecommerce.get_product", "GET", "ecommerce/v1/products/{product_id}", "none");
    // Electricity
    add("tgn.electricity.buy_token", "POST", "electricity/v1/tokens", "json");
    add("tgn.electricity.list_recent_purchases", "GET", "electricity/v1/purchases", "query");
    // Geo
    add("tgn.geo.get_user_location", "GET", "geo/v1/users/me/location", "none");
    add("tgn.geo.geocode_address", "GET", "geo/v1/geocode", "query");
    // Glocell
    add("tgn.glocell.list_products", "GET", "glocell/v1/products", "query");
    // Incentives
    add("tgn.incentives.get_qi_balance", "GET", "incentives/v1/qi/balance", "none");
    add("tgn.incentives.list_active_quests", "GET", "incentives/v1/quests/active", "query");
    // KiffStore
    add("tgn.kiffstore.search_items", "GET", "kiffstore/v1/items/search", "query");
    // Ledger
    add("tgn.ledger.get_account_balance", "GET", "ledger/v1/accounts/{account_id}/balance", "none");
    add("tgn.ledger.list_entries", "GET", "ledger/v1/accounts/{account_id}/entries", "query");
    // Localization
    add("tgn.localization.translate_text", "POST", "localization/v1/translate", "json");
    add("tgn.localization.list_supported_languages", "GET", "localization/v1/languages", "none");
    // Maps
    add("tgn.maps.geocode", "GET", "maps/v1/geocode", "query");
    add("tgn.maps.reverse_geocode", "GET", "maps/v1/reverse-geocode", "query");
    // MapsData
    add("tgn.mapsdata.search_pois", "GET", "mapsdata/v1/pois/search", "query");
    // Media
    add("tgn.media.create_upload_url", "POST", "media/v1/uploads", "json");
    add("tgn.media.get_media", "GET", "media/v1/media/{media_id}", "none");
    // Messaging
    add("tgn.messaging.send_message", "POST", "messaging/v1/messages", "json");
    add("tgn.messaging.list_conversations", "GET", "messaging/v1/conversations", "query");
    add("tgn.messaging.get_messages", "GET", "messaging/v1/conversations/{conversation_id}/messages", "query");
    // Notification
    add("tgn.notification.send_push", "POST", "notification/v1/push", "json");
    add("tgn.notification.list_for_user", "GET", "notification/v1/notifications", "query");
    // OpSupport
    add("tgn.opsupport.create_ticket", "POST", "opsupport/v1/tickets", "json");
    add("tgn.opsupport.get_system_status", "GET", "opsupport/v1/status", "none");
    // Panik
    add("tgn.panik.trigger_sos", "POST", "panik/v1/alerts", "json");
    add("tgn.panik.cancel_sos", "POST", "panik/v1/alerts/{alert_id}/cancel", "json");
    // Payfast
    add("tgn.payfast.create_payment", "POST", "payfast/v1/payments", "json");
    // Sdpkt
    add("tgn.sdpkt.get_balance", "GET", "sdpkt/v1/wallet/balance", "none");
    add("tgn.sdpkt.send_payment", "POST", "sdpkt/v1/wallet/transfers", "json");
    add("tgn.sdpkt.get_transactions", "GET", "sdpkt/v1/wallet/transactions", "query");
    // ShhMoney
    add("tgn.shhmoney.create_discreet_payment", "POST", "shhmoney/v1/payments", "json");
    // SleptOn
    add("tgn.slepton.list_stories", "GET", "slepton/v1/stories", "query");
    add("tgn.slepton.get_story", "GET", "slepton/v1/stories/{story_id}", "none");
    // SortedClothing
    add("tgn.sortedclothing.search_items", "GET", "sortedclothing/v1/items/search", "query");
    // TagMe
    add("tgn.tagme.create_tag", "POST", "tagme/v1/tags", "json");
    add("tgn.tagme.list_nearby_tags", "GET", "tagme/v1/tags/nearby", "query");
    // Takemehome
    add("tgn.takemehome.search_flights", "GET", "takemehome/v1/flights/search", "query");
    add("tgn.takemehome.search_stays", "GET", "takemehome/v1/stays/search", "query");
    // TheHotList
    add("tgn.thehotlist.list_entries", "GET", "thehotlist/v1/entries", "query");
    // TheJobCenter
    add("tgn.thejobcenter.search_jobs", "GET", "thejobcenter/v1/jobs/search", "query");
    add("tgn.thejobcenter.apply", "POST", "thejobcenter/v1/jobs/{job_id}/applications", "json");
    // ThirdParty
    add("tgn.thirdparty.list_integrations", "GET", "thirdparty/v1/integrations", "none");
    add("tgn.thirdparty.invoke_integration", "POST", "thirdparty/v1/integrations/{integration_name}/invoke", "json");
    // TrustSeal
    add("tgn.trustseal.get_status", "GET", "trustseal/v1/status", "none");
    add("tgn.trustseal.start_verification", "POST", "trustseal/v1/verifications", "json");
    // Wallet
    add("tgn.wallet.get_balance", "GET", "wallet/v1/balance", "query");
    add("tgn.wallet.get_transactions", "GET", "wallet/v1/transactions", "query");
    // WhatWeWant
    add("tgn.whatwewant.list_stories", "GET", "whatwewant/v1/stories", "query");
    add("tgn.whatwewant.get_story", "GET", "whatwewant/v1/stories/{story_id}", "none");
    // Wolverine
    add("tgn.wolverine.list_jobs", "GET", "wolverine/v1/jobs", "query");

    return m;
  }

  // ── URL / request building ───────────────────────────────────────────────────

  private resolveUrl(mapping: EndpointMapping, args: Args): string {
    // Substitute {placeholder} segments using arguments; substituted args are
    // stripped from the body/query below.
    let path = mapping.pathTemplate;
    for (const placeholder of extractPlaceholders(mapping.pathTemplate)) {
      const raw = args[placeholder];
      if (raw === null || raw === undefined) {
        throw new Error(
          `Tool argument '${placeholder}' is required to build URL '${mapping.pathTemplate}'.`,
        );
      }
      path = path.split("{" + placeholder + "}").join(encodeURIComponent(String(raw)));
    }

    let url = resolveUrl(this.baseUrl, path);

    if (mapping.body === "query") {
      const query = buildQueryString(buildBodyArgs(mapping, args));
      if (query.length > 0) {
        url += (url.includes("?") ? "&" : "?") + query;
      }
    }

    return url;
  }

  private static buildRequest(mapping: EndpointMapping, url: string, args: Args): HttpRequest {
    let body: string | undefined;
    if (mapping.body === "json") {
      body = JSON.stringify(buildBodyArgs(mapping, args));
    }
    const headers = new Map<string, string>();
    if (body !== undefined) headers.set("Content-Type", "application/json");
    return { method: mapping.method, url, headers, body };
  }
}

function buildBodyArgs(mapping: EndpointMapping, args: Args): Args {
  // Drop placeholders from the body/query — they're already in the URL.
  const placeholders = new Set(extractPlaceholders(mapping.pathTemplate));
  const result: Args = {};
  for (const key of Object.keys(args)) {
    if (placeholders.has(key)) continue;
    result[key] = args[key];
  }
  return result;
}

function extractPlaceholders(template: string): string[] {
  const out: string[] = [];
  let i = 0;
  while (i < template.length) {
    const open = template.indexOf("{", i);
    if (open < 0) break;
    const close = template.indexOf("}", open + 1);
    if (close < 0) break;
    out.push(template.substring(open + 1, close));
    i = close + 1;
  }
  return out;
}

function buildQueryString(args: Args): string {
  const parts: string[] = [];
  for (const key of Object.keys(args)) {
    const value = args[key];
    if (value === null || value === undefined) continue;
    const rendered = renderQueryValue(value);
    if (rendered === null) continue;
    parts.push(`${encodeURIComponent(key)}=${encodeURIComponent(rendered)}`);
  }
  return parts.join("&");
}

// eslint-disable-next-line @typescript-eslint/no-explicit-any
function renderQueryValue(value: any): string | null {
  if (typeof value === "string") return value;
  if (typeof value === "boolean") return value ? "true" : "false";
  if (typeof value === "number") return String(value);
  if (value === null || value === undefined) return null;
  return String(value);
}

/** Parse a response body as JSON; fall back to the raw string on parse failure / empty. */
// eslint-disable-next-line @typescript-eslint/no-explicit-any
function parseBody(body: string): any {
  if (body === undefined || body === null || body.length === 0) return null;
  try {
    return JSON.parse(body);
  } catch {
    return body;
  }
}

function isAbort(ex: unknown, signal?: AbortSignal): boolean {
  return signal?.aborted === true || (ex instanceof Error && ex.name === "AbortError");
}

function errorMessage(ex: unknown): string {
  return ex instanceof Error ? ex.message : String(ex);
}
