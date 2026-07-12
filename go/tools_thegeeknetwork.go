// tools_thegeeknetwork.go
//
// Ports the portable slice of CircleAI.Tools:
//
//	ToolDefinitionBuilder.cs     -> ToolDefinitionBuilder (fluent, validating)
//	ToolManifestGenerator.cs     -> GenerateJSONManifest / GenerateMarkdownManifest
//	DeviceDiagnosticsTools.cs    -> DeviceDiagnosticsTools() + DiagnoseFromContext
//	TheGeekNetworkTools.cs       -> per-API tool builders + TheGeekNetworkGetAllTools
//	HttpToolBridge.cs            -> HTTPToolBridge (routing + injected HTTP doer)
//	ComposioToolBridge.cs        -> ComposioToolBridge (JSON-RPC routing + parsing)
//
// ToolDefinition / ToolParameter / ToolInvocation / ToolResult / IToolBridge are
// already ported in tools.go — this file builds on them.
//
// HTTP INJECTION: the C# bridges take an HttpClient. The Go port takes a
// ToolHTTPDoer func(ctx, method, url, headers, body) (status, respBody,
// contentType, error) so the routing/URL-building/response-mapping logic is
// fully ported and unit-testable without pulling net/http into the flat package.
// A ready-made net/http adapter (NetToolHTTPDoer) is provided.

package circleai

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"sort"
	"strconv"
	"strings"
)

// ===========================================================================
// ToolDefinitionBuilder — ports ToolDefinitionBuilder.cs
// ===========================================================================

type toolBuilderParam struct {
	name      string
	parameter ToolParameter
	required  bool
}

// ToolDefinitionBuilder is a fluent builder for ToolDefinition. Ports
// ToolDefinitionBuilder. Construct with NewToolDefinitionBuilder.
type ToolDefinitionBuilder struct {
	name        string
	description string
	params      []toolBuilderParam
	err         error
}

// NewToolDefinitionBuilder starts a builder for a tool named name. Ports
// ToolDefinitionBuilder.Create. A blank name is captured as a deferred error
// surfaced by Build.
func NewToolDefinitionBuilder(name string) *ToolDefinitionBuilder {
	b := &ToolDefinitionBuilder{name: name}
	if name == "" {
		b.err = errors.New("name is required")
	}
	return b
}

// Description sets the tool description. Ports Description. Chainable; a blank
// description is captured as a deferred error.
func (b *ToolDefinitionBuilder) Description(description string) *ToolDefinitionBuilder {
	if description == "" {
		if b.err == nil {
			b.err = errors.New("description is required")
		}
		return b
	}
	b.description = description
	return b
}

// Parameter adds a parameter. Ports Parameter. Chainable; blank name/type/
// description is captured as a deferred error.
func (b *ToolDefinitionBuilder) Parameter(name, typ, description string, required bool, enumValues []string) *ToolDefinitionBuilder {
	if name == "" || typ == "" || description == "" {
		if b.err == nil {
			b.err = errors.New("parameter name, type, and description are required")
		}
		return b
	}
	b.params = append(b.params, toolBuilderParam{
		name:      name,
		parameter: ToolParameter{Type: typ, Description: description, Enum: enumValues},
		required:  required,
	})
	return b
}

// Build builds the ToolDefinition. Ports Build. Returns an error if a required
// field was missing (name/description) or a prior step failed.
func (b *ToolDefinitionBuilder) Build() (ToolDefinition, error) {
	if b.err != nil {
		return ToolDefinition{}, b.err
	}
	if b.description == "" {
		return ToolDefinition{}, fmt.Errorf("ToolDefinition '%s' requires a description. Call Description() before Build().", b.name)
	}
	params := make(map[string]ToolParameter, len(b.params))
	required := make([]string, 0)
	for _, p := range b.params {
		params[p.name] = p.parameter
		if p.required {
			required = append(required, p.name)
		}
	}
	return ToolDefinition{
		Name:               b.name,
		Description:        b.description,
		Parameters:         params,
		RequiredParameters: required,
	}, nil
}

// ===========================================================================
// ToolManifestGenerator — ports ToolManifestGenerator.cs
// ===========================================================================

// GenerateJSONManifest renders tools as an indented JSON array in OpenAI/Qwen
// function-calling format. Ports GenerateJsonManifest. Property maps are emitted
// with sorted keys for deterministic output.
func GenerateJSONManifest(tools []ToolDefinition) string {
	array := make([]map[string]any, 0, len(tools))
	for _, tool := range tools {
		properties := orderedJSONObject{}
		for _, key := range sortedParamKeys(tool.Parameters) {
			p := tool.Parameters[key]
			prop := orderedJSONObject{}
			prop.set("type", p.Type)
			prop.set("description", p.Description)
			if len(p.Enum) > 0 {
				prop.set("enum", p.Enum)
			}
			properties.set(key, prop)
		}
		parameters := orderedJSONObject{}
		parameters.set("type", "object")
		parameters.set("properties", properties)
		parameters.set("required", nonNilStrings(tool.RequiredParameters))

		function := orderedJSONObject{}
		function.set("name", tool.Name)
		function.set("description", tool.Description)
		function.set("parameters", parameters)

		entry := orderedJSONObject{}
		entry.set("type", "function")
		entry.set("function", function)
		array = append(array, map[string]any{"__ordered__": entry})
	}
	// Marshal via the ordered wrapper.
	wrapped := make([]orderedJSONObject, len(array))
	for i, m := range array {
		wrapped[i] = m["__ordered__"].(orderedJSONObject)
	}
	out, _ := json.MarshalIndent(wrapped, "", "  ")
	return string(out)
}

// GenerateMarkdownManifest renders tools as a human-readable Markdown summary
// grouped by API slug (the "tgn.<api>" prefix). Ports GenerateMarkdownManifest.
func GenerateMarkdownManifest(tools []ToolDefinition) string {
	var sb strings.Builder
	sb.WriteString("# Available Tools\n\n")
	sb.WriteString("Total: " + strconv.Itoa(len(tools)) + " tools.\n\n")

	groups := map[string][]ToolDefinition{}
	for _, tool := range tools {
		key := extractAPISlug(tool.Name)
		groups[key] = append(groups[key], tool)
	}
	groupKeys := make([]string, 0, len(groups))
	for k := range groups {
		groupKeys = append(groupKeys, k)
	}
	sort.Strings(groupKeys)

	for _, gk := range groupKeys {
		sb.WriteString("## " + gk + "\n\n")
		for _, tool := range groups[gk] {
			sb.WriteString("### `" + tool.Name + "`\n\n")
			sb.WriteString(tool.Description + "\n\n")
			if len(tool.Parameters) == 0 {
				sb.WriteString("_No parameters._\n\n")
				continue
			}
			sb.WriteString("Parameters:\n\n")
			sb.WriteString("| Name | Type | Required | Description |\n")
			sb.WriteString("|------|------|----------|-------------|\n")
			requiredSet := map[string]bool{}
			for _, r := range tool.RequiredParameters {
				requiredSet[r] = true
			}
			for _, key := range sortedParamKeys(tool.Parameters) {
				p := tool.Parameters[key]
				required := "no"
				if requiredSet[key] {
					required = "yes"
				}
				desc := escapePipe(p.Description)
				if len(p.Enum) > 0 {
					desc += " Allowed values: " + strings.Join(p.Enum, ", ") + "."
				}
				sb.WriteString("| `" + key + "` | " + p.Type + " | " + required + " | " + desc + " |\n")
			}
			sb.WriteString("\n")
		}
	}
	return sb.String()
}

func extractAPISlug(toolName string) string {
	const prefix = "tgn."
	if !strings.HasPrefix(toolName, prefix) {
		return toolName
	}
	rest := toolName[len(prefix):]
	if dot := strings.IndexByte(rest, '.'); dot >= 0 {
		return prefix + rest[:dot]
	}
	return prefix + rest
}

func escapePipe(s string) string { return strings.ReplaceAll(s, "|", "\\|") }

func sortedParamKeys(m map[string]ToolParameter) []string {
	keys := make([]string, 0, len(m))
	for k := range m {
		keys = append(keys, k)
	}
	sort.Strings(keys)
	return keys
}

// orderedJSONObject preserves key insertion order when marshalled, so the JSON
// manifest keeps the C# field ordering (type, description, enum / name,
// description, parameters).
type orderedJSONObject struct {
	keys   []string
	values map[string]any
}

func (o *orderedJSONObject) set(key string, value any) {
	if o.values == nil {
		o.values = map[string]any{}
	}
	if _, ok := o.values[key]; !ok {
		o.keys = append(o.keys, key)
	}
	o.values[key] = value
}

// MarshalJSON emits the object with keys in insertion order.
func (o orderedJSONObject) MarshalJSON() ([]byte, error) {
	var buf bytes.Buffer
	buf.WriteByte('{')
	for i, k := range o.keys {
		if i > 0 {
			buf.WriteByte(',')
		}
		kb, err := json.Marshal(k)
		if err != nil {
			return nil, err
		}
		buf.Write(kb)
		buf.WriteByte(':')
		vb, err := json.Marshal(o.values[k])
		if err != nil {
			return nil, err
		}
		buf.Write(vb)
	}
	buf.WriteByte('}')
	return buf.Bytes(), nil
}

// ===========================================================================
// DeviceDiagnosticsTools — ports DeviceDiagnosticsTools.cs
// ===========================================================================

// DeviceDiagnosticsTools returns the single device.diagnose tool definition.
// Ports DeviceDiagnosticsTools.Diagnostics.
func DeviceDiagnosticsTools() []ToolDefinition {
	return []ToolDefinition{
		{
			Name: "device.diagnose",
			Description: "Return a snapshot of the host device's health: CPU usage fraction, " +
				"available memory in MB, thermal state (normal/warm/critical), and " +
				"free storage in MB. Use before scheduling heavy inference to avoid " +
				"OOM conditions or OS thermal throttling.",
			Parameters:         map[string]ToolParameter{},
			RequiredParameters: []string{},
		},
	}
}

// DiagnoseFromContext reads an IDeviceContext and produces the compact JSON
// tool-output string. Ports DeviceDiagnosticsTools.DiagnoseFromContext. Nil
// members serialise as JSON null so the model distinguishes "unavailable" from
// zero. In the Go port ThermalState() already returns a lowercase string ("" =
// unavailable); CPUUsagePercent() is *float32; memory/storage are int64 bytes
// (0 = unavailable, rendered as null to match the C# nullable semantics).
func DiagnoseFromContext(ctx IDeviceContext) (string, error) {
	if ctx == nil {
		return "", errors.New("ctx is required")
	}
	frac := func(v *float32) string {
		if v == nil {
			return "null"
		}
		return strconv.FormatFloat(float64(*v), 'f', 3, 64)
	}
	longMB := func(v int64) string {
		if v <= 0 {
			return "null"
		}
		return strconv.FormatInt(v/(1024*1024), 10)
	}
	thermal := func(s string) string {
		if s == "" {
			return "null"
		}
		return "\"" + strings.ToLower(s) + "\""
	}
	return "{" +
		"\"cpu_usage_fraction\":" + frac(ctx.CPUUsagePercent()) + "," +
		"\"available_memory_mb\":" + longMB(ctx.AvailableMemoryBytes()) + "," +
		"\"thermal_state\":" + thermal(ctx.ThermalState()) + "," +
		"\"storage_free_mb\":" + longMB(ctx.StorageFreeBytes()) +
		"}", nil
}

// ===========================================================================
// TheGeekNetworkTools — ports TheGeekNetworkTools.cs
//
// Static catalogue of tool definitions covering the 36 APIs in the ecosystem.
// Tool names follow "tgn.<api_slug>.<verb>". Each API exposes 1-3 representative
// operations. The C# object-initialiser lists become td(...) calls with a
// compact params(...) helper.
// ===========================================================================

// tgnParam builds a ToolParameter. Ports the C# Param helper.
func tgnParam(typ, description string, enumValues ...string) ToolParameter {
	if len(enumValues) == 0 {
		enumValues = nil
	}
	return ToolParameter{Type: typ, Description: description, Enum: enumValues}
}

// tgnTool builds a ToolDefinition from ordered (name, ToolParameter) pairs plus
// the required-parameter names.
func tgnTool(name, description string, params map[string]ToolParameter, required ...string) ToolDefinition {
	if params == nil {
		params = map[string]ToolParameter{}
	}
	if required == nil {
		required = []string{}
	}
	return ToolDefinition{Name: name, Description: description, Parameters: params, RequiredParameters: required}
}

// TheGeekNetworkAccount ports TheGeekNetworkTools.Account.
func TheGeekNetworkAccount() []ToolDefinition {
	return []ToolDefinition{
		tgnTool("tgn.account.get_profile", "Get the authenticated user's account profile (display name, email, phone, country, KYC level).",
			map[string]ToolParameter{"user_id": tgnParam("string", "Target user ID. Use 'me' for the current authenticated user.")}, "user_id"),
		tgnTool("tgn.account.update_profile", "Update profile fields for the current user (display name, avatar, country).",
			map[string]ToolParameter{
				"display_name": tgnParam("string", "New display name. Optional."),
				"avatar_url":   tgnParam("string", "URL of the new avatar image. Optional."),
				"country_code": tgnParam("string", "ISO-3166 alpha-2 country code. Optional."),
			}),
	}
}

// TheGeekNetworkAudit ports TheGeekNetworkTools.Audit.
func TheGeekNetworkAudit() []ToolDefinition {
	return []ToolDefinition{
		tgnTool("tgn.audit.list_events", "List recent audit events for the authenticated user, optionally filtered by category.",
			map[string]ToolParameter{
				"category": tgnParam("string", "Optional event category filter (e.g. 'auth', 'payment', 'profile')."),
				"limit":    tgnParam("number", "Max number of events to return. Default 50, max 500."),
			}),
	}
}

// TheGeekNetworkAuth ports TheGeekNetworkTools.Auth.
func TheGeekNetworkAuth() []ToolDefinition {
	return []ToolDefinition{
		tgnTool("tgn.auth.request_otp", "Send a one-time password to the user's phone via SMS for login or sensitive action confirmation.",
			map[string]ToolParameter{
				"phone_number": tgnParam("string", "E.164-formatted phone number, e.g. +27821234567."),
				"purpose":      tgnParam("string", "Reason for the OTP.", "login", "signup", "transaction", "reset_pin"),
			}, "phone_number", "purpose"),
		tgnTool("tgn.auth.verify_otp", "Verify an OTP code previously sent to the user. Returns a session token on success.",
			map[string]ToolParameter{
				"phone_number": tgnParam("string", "E.164-formatted phone number."),
				"code":         tgnParam("string", "The OTP code the user received."),
			}, "phone_number", "code"),
		tgnTool("tgn.auth.push_to_app", "Trigger a push-to-app biometric approval on the user's mobile device for a web login or sensitive action.",
			map[string]ToolParameter{
				"session_id": tgnParam("string", "The web session awaiting approval."),
				"reason":     tgnParam("string", "Human-readable reason shown to the user on the device."),
			}, "session_id", "reason"),
	}
}

// TheGeekNetworkBidBaas ports TheGeekNetworkTools.BidBaas.
func TheGeekNetworkBidBaas() []ToolDefinition {
	return []ToolDefinition{
		tgnTool("tgn.bidbaas.list_active_auctions", "List currently active BidBaas auctions, optionally filtered by category or location.",
			map[string]ToolParameter{
				"category":     tgnParam("string", "Optional category filter, e.g. 'electronics', 'vehicles'."),
				"country_code": tgnParam("string", "Optional ISO-3166 country code."),
				"limit":        tgnParam("number", "Max number of auctions to return. Default 25."),
			}),
		tgnTool("tgn.bidbaas.place_bid", "Place a bid on an active BidBaas auction.",
			map[string]ToolParameter{
				"auction_id": tgnParam("string", "Auction identifier."),
				"amount":     tgnParam("number", "Bid amount in the auction's listed currency."),
				"currency":   tgnParam("string", "ISO-4217 currency code, e.g. 'ZAR', 'USD'."),
			}, "auction_id", "amount", "currency"),
		tgnTool("tgn.bidbaas.get_auction_details", "Get full details for a specific auction including current top bid, time remaining, and seller info.",
			map[string]ToolParameter{"auction_id": tgnParam("string", "Auction identifier.")}, "auction_id"),
	}
}

// TheGeekNetworkBillPayment ports TheGeekNetworkTools.BillPayment.
func TheGeekNetworkBillPayment() []ToolDefinition {
	return []ToolDefinition{
		tgnTool("tgn.billpayment.list_billers", "List available billers (utilities, telcos, councils) the user can pay.",
			map[string]ToolParameter{
				"country_code": tgnParam("string", "ISO-3166 country code, e.g. 'ZA'."),
				"category":     tgnParam("string", "Optional category filter, e.g. 'water', 'rates', 'data'."),
			}, "country_code"),
		tgnTool("tgn.billpayment.pay_bill", "Pay a bill for a specified biller using the user's wallet balance.",
			map[string]ToolParameter{
				"biller_id":      tgnParam("string", "Biller identifier from list_billers."),
				"account_number": tgnParam("string", "User's account number with that biller."),
				"amount":         tgnParam("number", "Amount to pay."),
				"currency":       tgnParam("string", "ISO-4217 currency code."),
			}, "biller_id", "account_number", "amount", "currency"),
	}
}

// TheGeekNetworkBlockchain ports TheGeekNetworkTools.Blockchain.
func TheGeekNetworkBlockchain() []ToolDefinition {
	return []ToolDefinition{
		tgnTool("tgn.blockchain.get_transaction", "Look up a SDPKT/Aether on-chain transaction by hash.",
			map[string]ToolParameter{"tx_hash": tgnParam("string", "Transaction hash.")}, "tx_hash"),
		tgnTool("tgn.blockchain.get_address_info", "Get on-chain info about an Aether address (balance, recent activity).",
			map[string]ToolParameter{"address": tgnParam("string", "Aether wallet address.")}, "address"),
	}
}

// TheGeekNetworkButler ports TheGeekNetworkTools.Butler.
func TheGeekNetworkButler() []ToolDefinition {
	return []ToolDefinition{
		tgnTool("tgn.butler.log_interaction", "Log a B!/Butler interaction for analytics and personalisation.",
			map[string]ToolParameter{
				"intent":     tgnParam("string", "Detected intent name."),
				"transcript": tgnParam("string", "Raw user utterance, redacted as needed."),
				"success":    tgnParam("boolean", "Whether the action succeeded."),
			}, "intent", "transcript", "success"),
		tgnTool("tgn.butler.get_user_context", "Fetch the server-side context for the current user (recent intents, preferences, capabilities).",
			map[string]ToolParameter{"user_id": tgnParam("string", "Target user ID. Use 'me' for the current user.")}, "user_id"),
	}
}

// TheGeekNetworkCircleAether ports TheGeekNetworkTools.CircleAether.
func TheGeekNetworkCircleAether() []ToolDefinition {
	return []ToolDefinition{
		tgnTool("tgn.circleaether.get_node_status", "Get current mesh-node status (peers, throughput, region) for the authenticated device.",
			map[string]ToolParameter{"device_id": tgnParam("string", "Device identifier. Use 'this' for the current device.")}, "device_id"),
		tgnTool("tgn.circleaether.list_nearby_peers", "List mesh peers reachable from the current node, with link quality and tipping eligibility.",
			map[string]ToolParameter{"max_peers": tgnParam("number", "Max number of peers to return. Default 25.")}),
	}
}

// TheGeekNetworkEcommerce ports TheGeekNetworkTools.Ecommerce.
func TheGeekNetworkEcommerce() []ToolDefinition {
	return []ToolDefinition{
		tgnTool("tgn.ecommerce.search_products", "Search the unified product catalogue across merchants in the ecosystem.",
			map[string]ToolParameter{
				"query":     tgnParam("string", "Free-text search query."),
				"category":  tgnParam("string", "Optional category filter."),
				"max_price": tgnParam("number", "Optional maximum price."),
				"currency":  tgnParam("string", "ISO-4217 currency code."),
			}, "query"),
		tgnTool("tgn.ecommerce.get_product", "Get full product details by ID, including stock, variants, and merchant info.",
			map[string]ToolParameter{"product_id": tgnParam("string", "Product identifier.")}, "product_id"),
	}
}

// TheGeekNetworkElectricity ports TheGeekNetworkTools.Electricity.
func TheGeekNetworkElectricity() []ToolDefinition {
	return []ToolDefinition{
		tgnTool("tgn.electricity.buy_token", "Buy prepaid electricity for a meter and return the STS token to enter into the meter.",
			map[string]ToolParameter{
				"meter_number": tgnParam("string", "11-digit meter number."),
				"amount":       tgnParam("number", "Amount to spend on electricity."),
				"currency":     tgnParam("string", "ISO-4217 currency code, typically 'ZAR'."),
			}, "meter_number", "amount", "currency"),
		tgnTool("tgn.electricity.list_recent_purchases", "List the user's recent prepaid-electricity purchases.",
			map[string]ToolParameter{"limit": tgnParam("number", "Max number of purchases to return. Default 10.")}),
	}
}

// TheGeekNetworkGeo ports TheGeekNetworkTools.Geo.
func TheGeekNetworkGeo() []ToolDefinition {
	return []ToolDefinition{
		tgnTool("tgn.geo.get_user_location", "Get the authenticated user's current best-known location (lat/lng, accuracy, country).", nil),
		tgnTool("tgn.geo.geocode_address", "Convert a human-readable address to coordinates.",
			map[string]ToolParameter{
				"address":      tgnParam("string", "Free-text address to geocode."),
				"country_code": tgnParam("string", "Optional ISO-3166 country bias."),
			}, "address"),
	}
}

// TheGeekNetworkGlocell ports TheGeekNetworkTools.Glocell.
func TheGeekNetworkGlocell() []ToolDefinition {
	return []ToolDefinition{
		tgnTool("tgn.glocell.list_products", "List Glocell retail products (airtime, data, vouchers) available to the user.",
			map[string]ToolParameter{"category": tgnParam("string", "Optional category filter, e.g. 'airtime', 'data'.")}),
	}
}

// TheGeekNetworkIncentives ports TheGeekNetworkTools.Incentives.
func TheGeekNetworkIncentives() []ToolDefinition {
	return []ToolDefinition{
		tgnTool("tgn.incentives.get_qi_balance", "Get the user's current Qi (and Karma) balance and earning streak.", nil),
		tgnTool("tgn.incentives.list_active_quests", "List quests/challenges the user can complete to earn Qi.",
			map[string]ToolParameter{"limit": tgnParam("number", "Max number of quests to return. Default 10.")}),
	}
}

// TheGeekNetworkKiffStore ports TheGeekNetworkTools.KiffStore.
func TheGeekNetworkKiffStore() []ToolDefinition {
	return []ToolDefinition{
		tgnTool("tgn.kiffstore.search_items", "Search KiffStore listings.",
			map[string]ToolParameter{
				"query": tgnParam("string", "Free-text search query."),
				"limit": tgnParam("number", "Max number of results. Default 25."),
			}, "query"),
	}
}

// TheGeekNetworkLedger ports TheGeekNetworkTools.Ledger.
func TheGeekNetworkLedger() []ToolDefinition {
	return []ToolDefinition{
		tgnTool("tgn.ledger.get_account_balance", "Get the running balance for a ledger account belonging to the user.",
			map[string]ToolParameter{"account_id": tgnParam("string", "Ledger account identifier.")}, "account_id"),
		tgnTool("tgn.ledger.list_entries", "List ledger entries for an account in reverse chronological order.",
			map[string]ToolParameter{
				"account_id": tgnParam("string", "Ledger account identifier."),
				"limit":      tgnParam("number", "Max number of entries to return. Default 50."),
			}, "account_id"),
	}
}

// TheGeekNetworkLocalization ports TheGeekNetworkTools.Localization.
func TheGeekNetworkLocalization() []ToolDefinition {
	return []ToolDefinition{
		tgnTool("tgn.localization.translate_text", "Translate a piece of text from one language to another using the ecosystem translation service.",
			map[string]ToolParameter{
				"text":            tgnParam("string", "Text to translate."),
				"source_language": tgnParam("string", "ISO-639-1 source code or 'auto' for auto-detect."),
				"target_language": tgnParam("string", "ISO-639-1 target code, e.g. 'en', 'zu', 'fr'."),
			}, "text", "target_language"),
		tgnTool("tgn.localization.list_supported_languages", "List all language codes supported by the ecosystem.", nil),
	}
}

// TheGeekNetworkMaps ports TheGeekNetworkTools.Maps.
func TheGeekNetworkMaps() []ToolDefinition {
	return []ToolDefinition{
		tgnTool("tgn.maps.geocode", "Forward-geocode an address to coordinates via DataAcuity.",
			map[string]ToolParameter{"address": tgnParam("string", "Free-text address.")}, "address"),
		tgnTool("tgn.maps.reverse_geocode", "Reverse-geocode coordinates to an address.",
			map[string]ToolParameter{
				"latitude":  tgnParam("number", "Latitude in decimal degrees."),
				"longitude": tgnParam("number", "Longitude in decimal degrees."),
			}, "latitude", "longitude"),
	}
}

// TheGeekNetworkMapsData ports TheGeekNetworkTools.MapsData.
func TheGeekNetworkMapsData() []ToolDefinition {
	return []ToolDefinition{
		tgnTool("tgn.mapsdata.search_pois", "Search points of interest near a location, filtered by category.",
			map[string]ToolParameter{
				"latitude":      tgnParam("number", "Latitude in decimal degrees."),
				"longitude":     tgnParam("number", "Longitude in decimal degrees."),
				"radius_meters": tgnParam("number", "Search radius in metres. Default 1000."),
				"category":      tgnParam("string", "Optional POI category, e.g. 'pharmacy', 'fuel'."),
			}, "latitude", "longitude"),
	}
}

// TheGeekNetworkMedia ports TheGeekNetworkTools.Media.
func TheGeekNetworkMedia() []ToolDefinition {
	return []ToolDefinition{
		tgnTool("tgn.media.create_upload_url", "Create a pre-signed URL the client can PUT a media file to. Does not upload the file itself.",
			map[string]ToolParameter{
				"mime_type":  tgnParam("string", "MIME type of the file, e.g. 'image/jpeg'."),
				"size_bytes": tgnParam("number", "File size in bytes."),
			}, "mime_type", "size_bytes"),
		tgnTool("tgn.media.get_media", "Get metadata and a viewable URL for a previously uploaded media item.",
			map[string]ToolParameter{"media_id": tgnParam("string", "Media identifier.")}, "media_id"),
	}
}

// TheGeekNetworkMessaging ports TheGeekNetworkTools.Messaging.
func TheGeekNetworkMessaging() []ToolDefinition {
	return []ToolDefinition{
		tgnTool("tgn.messaging.send_message", "Send a TxTMe message to a contact or conversation.",
			map[string]ToolParameter{
				"recipient":       tgnParam("string", "Recipient identifier - phone number (E.164) or user_id."),
				"body":            tgnParam("string", "Message body."),
				"conversation_id": tgnParam("string", "Optional existing conversation to post into."),
			}, "recipient", "body"),
		tgnTool("tgn.messaging.list_conversations", "List the user's active TxTMe conversations, most recent first.",
			map[string]ToolParameter{"limit": tgnParam("number", "Max number of conversations to return. Default 25.")}),
		tgnTool("tgn.messaging.get_messages", "Get messages in a specific conversation, most recent first.",
			map[string]ToolParameter{
				"conversation_id": tgnParam("string", "Conversation identifier."),
				"limit":           tgnParam("number", "Max number of messages to return. Default 50."),
			}, "conversation_id"),
	}
}

// TheGeekNetworkNotification ports TheGeekNetworkTools.Notification.
func TheGeekNetworkNotification() []ToolDefinition {
	return []ToolDefinition{
		tgnTool("tgn.notification.send_push", "Send a push notification to a user's registered devices.",
			map[string]ToolParameter{
				"user_id": tgnParam("string", "Target user ID."),
				"title":   tgnParam("string", "Notification title."),
				"body":    tgnParam("string", "Notification body text."),
				"data":    tgnParam("object", "Optional structured payload for the app to handle."),
			}, "user_id", "title", "body"),
		tgnTool("tgn.notification.list_for_user", "List recent in-app notifications for the authenticated user.",
			map[string]ToolParameter{
				"unread_only": tgnParam("boolean", "If true, return only unread notifications. Default false."),
				"limit":       tgnParam("number", "Max number to return. Default 50."),
			}),
	}
}

// TheGeekNetworkOpSupport ports TheGeekNetworkTools.OpSupport.
func TheGeekNetworkOpSupport() []ToolDefinition {
	return []ToolDefinition{
		tgnTool("tgn.opsupport.create_ticket", "File a support ticket on the user's behalf.",
			map[string]ToolParameter{
				"category": tgnParam("string", "Ticket category.", "billing", "account", "bug", "feature_request", "other"),
				"subject":  tgnParam("string", "Short subject line."),
				"body":     tgnParam("string", "Full description of the issue."),
			}, "category", "subject", "body"),
		tgnTool("tgn.opsupport.get_system_status", "Get current system / API status (uptime, incidents).", nil),
	}
}

// TheGeekNetworkPanik ports TheGeekNetworkTools.Panik.
func TheGeekNetworkPanik() []ToolDefinition {
	return []ToolDefinition{
		tgnTool("tgn.panik.trigger_sos", "Trigger an SOS emergency alert. Notifies the user's panic contacts and optionally dispatches help.",
			map[string]ToolParameter{
				"latitude":  tgnParam("number", "Current latitude in decimal degrees."),
				"longitude": tgnParam("number", "Current longitude in decimal degrees."),
				"category":  tgnParam("string", "Type of emergency.", "medical", "crime", "fire", "accident", "other"),
				"note":      tgnParam("string", "Optional short note describing the emergency."),
			}, "latitude", "longitude", "category"),
		tgnTool("tgn.panik.cancel_sos", "Cancel an in-progress SOS alert raised by the current user.",
			map[string]ToolParameter{
				"alert_id": tgnParam("string", "SOS alert identifier."),
				"reason":   tgnParam("string", "Optional reason for cancellation."),
			}, "alert_id"),
	}
}

// TheGeekNetworkPayfast ports TheGeekNetworkTools.Payfast.
func TheGeekNetworkPayfast() []ToolDefinition {
	return []ToolDefinition{
		tgnTool("tgn.payfast.create_payment", "Create a PayFast payment intent and return the redirect URL the user should open.",
			map[string]ToolParameter{
				"amount":     tgnParam("number", "Amount to charge."),
				"currency":   tgnParam("string", "ISO-4217 currency code, e.g. 'ZAR'."),
				"item_name":  tgnParam("string", "Short description shown on the PayFast page."),
				"return_url": tgnParam("string", "URL to return to on completion."),
			}, "amount", "currency", "item_name"),
	}
}

// TheGeekNetworkSdpkt ports TheGeekNetworkTools.Sdpkt.
func TheGeekNetworkSdpkt() []ToolDefinition {
	return []ToolDefinition{
		tgnTool("tgn.sdpkt.get_balance", "Get the user's SDPKT wallet balance, including any sub-balances (Qi, Karma, fiat-pegged).", nil),
		tgnTool("tgn.sdpkt.send_payment", "Send an SDPKT payment to another user or wallet address.",
			map[string]ToolParameter{
				"recipient": tgnParam("string", "Recipient identifier - user ID, phone number (E.164), or wallet address."),
				"amount":    tgnParam("number", "Amount to send."),
				"currency":  tgnParam("string", "Currency code: 'SDPKT', 'QI', 'KARMA', or fiat ISO-4217."),
				"memo":      tgnParam("string", "Optional memo attached to the transaction."),
			}, "recipient", "amount", "currency"),
		tgnTool("tgn.sdpkt.get_transactions", "List the user's recent SDPKT wallet transactions.",
			map[string]ToolParameter{"limit": tgnParam("number", "Max number of transactions to return. Default 25.")}),
	}
}

// TheGeekNetworkShhMoney ports TheGeekNetworkTools.ShhMoney.
func TheGeekNetworkShhMoney() []ToolDefinition {
	return []ToolDefinition{
		tgnTool("tgn.shhmoney.create_discreet_payment", "Create a discreet ShhMoney payment - sender and recipient identifiers are hidden from third parties on the ledger surface.",
			map[string]ToolParameter{
				"recipient": tgnParam("string", "Recipient identifier."),
				"amount":    tgnParam("number", "Amount to send."),
				"currency":  tgnParam("string", "ISO-4217 currency code."),
			}, "recipient", "amount", "currency"),
	}
}

// TheGeekNetworkSleptOn ports TheGeekNetworkTools.SleptOn.
func TheGeekNetworkSleptOn() []ToolDefinition {
	return []ToolDefinition{
		tgnTool("tgn.slepton.list_stories", "List recent SleptOn stories, optionally filtered by topic or country.",
			map[string]ToolParameter{
				"topic":        tgnParam("string", "Optional topic filter."),
				"country_code": tgnParam("string", "Optional ISO-3166 country code."),
				"limit":        tgnParam("number", "Max number of stories. Default 25."),
			}),
		tgnTool("tgn.slepton.get_story", "Get a SleptOn story's full body and metadata.",
			map[string]ToolParameter{"story_id": tgnParam("string", "Story identifier.")}, "story_id"),
	}
}

// TheGeekNetworkSortedClothing ports TheGeekNetworkTools.SortedClothing.
func TheGeekNetworkSortedClothing() []ToolDefinition {
	return []ToolDefinition{
		tgnTool("tgn.sortedclothing.search_items", "Search the SortedClothing inventory.",
			map[string]ToolParameter{
				"query": tgnParam("string", "Free-text search query."),
				"size":  tgnParam("string", "Optional size filter."),
				"limit": tgnParam("number", "Max results. Default 25."),
			}, "query"),
	}
}

// TheGeekNetworkTagMe ports TheGeekNetworkTools.TagMe.
func TheGeekNetworkTagMe() []ToolDefinition {
	return []ToolDefinition{
		tgnTool("tgn.tagme.create_tag", "Create a geo-tag at a location with optional note and visibility.",
			map[string]ToolParameter{
				"latitude":   tgnParam("number", "Latitude in decimal degrees."),
				"longitude":  tgnParam("number", "Longitude in decimal degrees."),
				"note":       tgnParam("string", "Optional text note."),
				"visibility": tgnParam("string", "Who can see the tag.", "public", "friends", "private"),
			}, "latitude", "longitude"),
		tgnTool("tgn.tagme.list_nearby_tags", "List geo-tags near a location.",
			map[string]ToolParameter{
				"latitude":      tgnParam("number", "Latitude in decimal degrees."),
				"longitude":     tgnParam("number", "Longitude in decimal degrees."),
				"radius_meters": tgnParam("number", "Radius in metres. Default 500."),
			}, "latitude", "longitude"),
	}
}

// TheGeekNetworkTakemehome ports TheGeekNetworkTools.Takemehome.
func TheGeekNetworkTakemehome() []ToolDefinition {
	return []ToolDefinition{
		tgnTool("tgn.takemehome.search_flights", "Search flights across multiple suppliers and return ranked options.",
			map[string]ToolParameter{
				"origin":      tgnParam("string", "Origin IATA code or city name."),
				"destination": tgnParam("string", "Destination IATA code or city name."),
				"depart_date": tgnParam("string", "Departure date in YYYY-MM-DD."),
				"return_date": tgnParam("string", "Optional return date in YYYY-MM-DD."),
				"passengers":  tgnParam("number", "Number of passengers. Default 1."),
			}, "origin", "destination", "depart_date"),
		tgnTool("tgn.takemehome.search_stays", "Search accommodation options for a destination and date range.",
			map[string]ToolParameter{
				"destination": tgnParam("string", "Destination city or area."),
				"check_in":    tgnParam("string", "Check-in date in YYYY-MM-DD."),
				"check_out":   tgnParam("string", "Check-out date in YYYY-MM-DD."),
				"guests":      tgnParam("number", "Number of guests. Default 1."),
			}, "destination", "check_in", "check_out"),
	}
}

// TheGeekNetworkTheHotList ports TheGeekNetworkTools.TheHotList.
func TheGeekNetworkTheHotList() []ToolDefinition {
	return []ToolDefinition{
		tgnTool("tgn.thehotlist.list_entries", "List curated 'hot list' entries, optionally filtered by category or country.",
			map[string]ToolParameter{
				"category":     tgnParam("string", "Optional category filter."),
				"country_code": tgnParam("string", "Optional ISO-3166 country code."),
				"limit":        tgnParam("number", "Max entries to return. Default 25."),
			}),
	}
}

// TheGeekNetworkTheJobCenter ports TheGeekNetworkTools.TheJobCenter.
func TheGeekNetworkTheJobCenter() []ToolDefinition {
	return []ToolDefinition{
		tgnTool("tgn.thejobcenter.search_jobs", "Search job postings.",
			map[string]ToolParameter{
				"query":        tgnParam("string", "Free-text search query, e.g. 'plumber Cape Town'."),
				"country_code": tgnParam("string", "Optional ISO-3166 country code."),
				"limit":        tgnParam("number", "Max results. Default 25."),
			}, "query"),
		tgnTool("tgn.thejobcenter.apply", "Submit an application to a job posting on the user's behalf.",
			map[string]ToolParameter{
				"job_id":     tgnParam("string", "Job posting identifier."),
				"cover_note": tgnParam("string", "Optional cover note."),
			}, "job_id"),
	}
}

// TheGeekNetworkThirdParty ports TheGeekNetworkTools.ThirdParty.
func TheGeekNetworkThirdParty() []ToolDefinition {
	return []ToolDefinition{
		tgnTool("tgn.thirdparty.list_integrations", "List configured third-party integrations available to the user (e.g. Xero, Zapier-style hooks).", nil),
		tgnTool("tgn.thirdparty.invoke_integration", "Invoke a registered third-party integration by name with a JSON payload.",
			map[string]ToolParameter{
				"integration_name": tgnParam("string", "Integration name from list_integrations."),
				"payload":          tgnParam("object", "JSON payload to forward to the integration."),
			}, "integration_name", "payload"),
	}
}

// TheGeekNetworkTrustSeal ports TheGeekNetworkTools.TrustSeal.
func TheGeekNetworkTrustSeal() []ToolDefinition {
	return []ToolDefinition{
		tgnTool("tgn.trustseal.get_status", "Get the user's TrustSeal verification status (KYC level, document checks).", nil),
		tgnTool("tgn.trustseal.start_verification", "Start a verification flow for a specified KYC level.",
			map[string]ToolParameter{"level": tgnParam("string", "Target KYC level.", "basic", "verified", "enhanced")}, "level"),
	}
}

// TheGeekNetworkWallet ports TheGeekNetworkTools.Wallet.
func TheGeekNetworkWallet() []ToolDefinition {
	return []ToolDefinition{
		tgnTool("tgn.wallet.get_balance", "Get the user's wallet balance(s) across all supported currencies.",
			map[string]ToolParameter{"currency": tgnParam("string", "Optional ISO-4217 currency to restrict the balance to.")}),
		tgnTool("tgn.wallet.get_transactions", "List the user's recent wallet transactions.",
			map[string]ToolParameter{
				"currency": tgnParam("string", "Optional ISO-4217 currency filter."),
				"limit":    tgnParam("number", "Max transactions to return. Default 25."),
			}),
	}
}

// TheGeekNetworkWhatWeWant ports TheGeekNetworkTools.WhatWeWant.
func TheGeekNetworkWhatWeWant() []ToolDefinition {
	return []ToolDefinition{
		tgnTool("tgn.whatwewant.list_stories", "List WhatWeWant stories, sorted by recency.",
			map[string]ToolParameter{
				"topic": tgnParam("string", "Optional topic filter."),
				"limit": tgnParam("number", "Max stories to return. Default 25."),
			}),
		tgnTool("tgn.whatwewant.get_story", "Get a single WhatWeWant story's full body and metadata.",
			map[string]ToolParameter{"story_id": tgnParam("string", "Story identifier.")}, "story_id"),
	}
}

// TheGeekNetworkWolverine ports TheGeekNetworkTools.Wolverine.
func TheGeekNetworkWolverine() []ToolDefinition {
	return []ToolDefinition{
		tgnTool("tgn.wolverine.list_jobs", "List background jobs visible to the user (status, last run, next run).",
			map[string]ToolParameter{"status": tgnParam("string", "Optional status filter.", "queued", "running", "succeeded", "failed")}),
	}
}

// TheGeekNetworkGetAllTools concatenates every API's tools into one canonical
// list. Ports TheGeekNetworkTools.GetAllTools.
func TheGeekNetworkGetAllTools() []ToolDefinition {
	groups := [][]ToolDefinition{
		TheGeekNetworkAccount(), TheGeekNetworkAudit(), TheGeekNetworkAuth(), TheGeekNetworkBidBaas(),
		TheGeekNetworkBillPayment(), TheGeekNetworkBlockchain(), TheGeekNetworkButler(), TheGeekNetworkCircleAether(),
		TheGeekNetworkEcommerce(), TheGeekNetworkElectricity(), TheGeekNetworkGeo(), TheGeekNetworkGlocell(),
		TheGeekNetworkIncentives(), TheGeekNetworkKiffStore(), TheGeekNetworkLedger(), TheGeekNetworkLocalization(),
		TheGeekNetworkMaps(), TheGeekNetworkMapsData(), TheGeekNetworkMedia(), TheGeekNetworkMessaging(),
		TheGeekNetworkNotification(), TheGeekNetworkOpSupport(), TheGeekNetworkPanik(), TheGeekNetworkPayfast(),
		TheGeekNetworkSdpkt(), TheGeekNetworkShhMoney(), TheGeekNetworkSleptOn(), TheGeekNetworkSortedClothing(),
		TheGeekNetworkTagMe(), TheGeekNetworkTakemehome(), TheGeekNetworkTheHotList(), TheGeekNetworkTheJobCenter(),
		TheGeekNetworkThirdParty(), TheGeekNetworkTrustSeal(), TheGeekNetworkWallet(), TheGeekNetworkWhatWeWant(),
		TheGeekNetworkWolverine(),
	}
	all := make([]ToolDefinition, 0, 96)
	for _, g := range groups {
		all = append(all, g...)
	}
	return all
}

// ===========================================================================
// HTTP injection surface + bridges — port HttpToolBridge.cs / ComposioToolBridge.cs
// ===========================================================================

// ToolHTTPResponse is the outcome of a ToolHTTPDoer call.
type ToolHTTPResponse struct {
	StatusCode  int
	Body        []byte
	ContentType string
}

// ToolHTTPDoer performs one HTTP request. It is the injection seam for the tool
// bridges: the C# HttpClient dependency becomes this func so the routing logic
// is unit-testable. headers may be nil; body may be nil for GET. (Named
// Tool-prefixed to avoid clashing with the interface-shaped HTTPDoer used by the
// OpenAI-compatible generator.)
type ToolHTTPDoer func(ctx context.Context, method, url string, headers map[string]string, body []byte) (ToolHTTPResponse, error)

// NetToolHTTPDoer adapts a *http.Client into a ToolHTTPDoer. Pass
// http.DefaultClient for the standard client.
func NetToolHTTPDoer(client *http.Client) ToolHTTPDoer {
	if client == nil {
		client = http.DefaultClient
	}
	return func(ctx context.Context, method, u string, headers map[string]string, body []byte) (ToolHTTPResponse, error) {
		var rdr io.Reader
		if body != nil {
			rdr = bytes.NewReader(body)
		}
		req, err := http.NewRequestWithContext(ctx, method, u, rdr)
		if err != nil {
			return ToolHTTPResponse{}, err
		}
		for k, v := range headers {
			req.Header.Set(k, v)
		}
		resp, err := client.Do(req)
		if err != nil {
			return ToolHTTPResponse{}, err
		}
		defer resp.Body.Close()
		data, err := io.ReadAll(resp.Body)
		if err != nil {
			return ToolHTTPResponse{}, err
		}
		return ToolHTTPResponse{StatusCode: resp.StatusCode, Body: data, ContentType: resp.Header.Get("Content-Type")}, nil
	}
}

// httpBodyStrategy values mirror the C# BodyNone/BodyQuery/BodyJson consts.
type httpBodyStrategy int

const (
	bodyNone httpBodyStrategy = iota
	bodyQuery
	bodyJSON
)

type endpointMapping struct {
	method       string
	pathTemplate string
	body         httpBodyStrategy
}

// HTTPToolBridge routes tool calls to the TheGeekNetwork REST APIs. Ports
// HttpToolBridge. Construct with NewHTTPToolBridge. No HTTP happens during
// construction or in AvailableTools — only Invoke hits the wire (via the
// injected ToolHTTPDoer).
type HTTPToolBridge struct {
	doer    ToolHTTPDoer
	baseURL string // guaranteed to end with '/'
	tools   []ToolDefinition
	routes  map[string]endpointMapping
}

// NewHTTPToolBridge constructs the bridge over baseURL + an injected doer, using
// the full TheGeekNetwork catalogue. Ports the two-arg C# constructor. Returns
// an error if baseURL is blank or doer is nil.
func NewHTTPToolBridge(baseURL string, doer ToolHTTPDoer) (*HTTPToolBridge, error) {
	return NewHTTPToolBridgeWithTools(baseURL, doer, TheGeekNetworkGetAllTools())
}

// NewHTTPToolBridgeWithTools constructs the bridge with a custom tool list.
// Ports the three-arg C# constructor.
func NewHTTPToolBridgeWithTools(baseURL string, doer ToolHTTPDoer, tools []ToolDefinition) (*HTTPToolBridge, error) {
	if strings.TrimSpace(baseURL) == "" {
		return nil, errors.New("baseUrl required")
	}
	if doer == nil {
		return nil, errors.New("doer required")
	}
	if !strings.HasSuffix(baseURL, "/") {
		baseURL += "/"
	}
	return &HTTPToolBridge{
		doer:    doer,
		baseURL: baseURL,
		tools:   tools,
		routes:  httpToolBridgeRoutes(),
	}, nil
}

// AvailableTools returns the bridge's tool list. Ports the AvailableTools
// property.
func (b *HTTPToolBridge) AvailableTools() []ToolDefinition { return b.tools }

// GetAvailableTools returns the static tool list (no remote query). Ports the
// default GetAvailableToolsAsync.
func (b *HTTPToolBridge) GetAvailableTools(ctx context.Context) ([]ToolDefinition, error) {
	return b.tools, nil
}

// Invoke routes an invocation to its mapped endpoint and maps the response to a
// ToolResult. Ports InvokeAsync. An unmapped tool returns a not-registered
// failure result (not an error). A non-2xx response returns a failure result
// carrying the parsed body. Only a doer transport error is returned as a Go
// error.
func (b *HTTPToolBridge) Invoke(ctx context.Context, invocation ToolInvocation) (ToolResult, error) {
	mapping, ok := b.routes[invocation.ToolName]
	if !ok {
		return ToolResult{
			ToolName: invocation.ToolName,
			Success:  false,
			Error:    "Tool '" + invocation.ToolName + "' is not registered in this bridge instance.",
		}, nil
	}

	url, err := b.resolveURL(mapping, invocation.Arguments)
	if err != nil {
		return ToolResult{ToolName: invocation.ToolName, Success: false, Error: err.Error()}, nil
	}
	var reqBody []byte
	headers := map[string]string{}
	if mapping.body == bodyJSON {
		payload := buildBodyArgs(mapping, invocation.Arguments)
		reqBody, _ = json.Marshal(payload)
		headers["Content-Type"] = "application/json"
	}

	resp, err := b.doer(ctx, mapping.method, url, headers, reqBody)
	if err != nil {
		return ToolResult{}, err
	}

	var bodyVal any
	if len(resp.Body) > 0 {
		if strings.Contains(strings.ToLower(resp.ContentType), "json") {
			var parsed any
			if json.Unmarshal(resp.Body, &parsed) == nil {
				bodyVal = parsed
			} else {
				bodyVal = string(resp.Body)
			}
		} else {
			bodyVal = string(resp.Body)
		}
	}

	if resp.StatusCode < 200 || resp.StatusCode >= 300 {
		return ToolResult{
			ToolName: invocation.ToolName,
			Success:  false,
			Result:   bodyVal,
			Error:    "HTTP " + strconv.Itoa(resp.StatusCode) + " " + http.StatusText(resp.StatusCode),
		}, nil
	}
	return ToolResult{ToolName: invocation.ToolName, Success: true, Result: bodyVal}, nil
}

func (b *HTTPToolBridge) resolveURL(mapping endpointMapping, arguments map[string]any) (string, error) {
	path := mapping.pathTemplate
	for _, placeholder := range extractPlaceholders(mapping.pathTemplate) {
		raw, ok := arguments[placeholder]
		if !ok || raw == nil {
			return "", errors.New("Tool argument '" + placeholder + "' is required to build URL '" + mapping.pathTemplate + "'.")
		}
		path = strings.ReplaceAll(path, "{"+placeholder+"}", url.PathEscape(renderArgString(raw)))
	}
	full := b.baseURL + path
	if mapping.body == bodyQuery {
		query := buildQueryString(buildBodyArgs(mapping, arguments))
		if query != "" {
			if strings.Contains(full, "?") {
				full += "&" + query
			} else {
				full += "?" + query
			}
		}
	}
	return full, nil
}

func buildBodyArgs(mapping endpointMapping, arguments map[string]any) map[string]any {
	placeholders := map[string]bool{}
	for _, p := range extractPlaceholders(mapping.pathTemplate) {
		placeholders[p] = true
	}
	result := map[string]any{}
	for k, v := range arguments {
		if placeholders[k] {
			continue
		}
		result[k] = v
	}
	return result
}

func extractPlaceholders(template string) []string {
	var out []string
	i := 0
	for i < len(template) {
		open := strings.IndexByte(template[i:], '{')
		if open < 0 {
			break
		}
		open += i
		close := strings.IndexByte(template[open+1:], '}')
		if close < 0 {
			break
		}
		close += open + 1
		out = append(out, template[open+1:close])
		i = close + 1
	}
	return out
}

func buildQueryString(args map[string]any) string {
	// Sort keys for deterministic query strings.
	keys := make([]string, 0, len(args))
	for k := range args {
		keys = append(keys, k)
	}
	sort.Strings(keys)
	var sb strings.Builder
	first := true
	for _, k := range keys {
		v := args[k]
		if v == nil {
			continue
		}
		rendered := renderQueryValue(v)
		if rendered == "" && v == nil {
			continue
		}
		if !first {
			sb.WriteByte('&')
		}
		sb.WriteString(url.QueryEscape(k))
		sb.WriteByte('=')
		sb.WriteString(url.QueryEscape(rendered))
		first = false
	}
	return sb.String()
}

func renderArgString(v any) string {
	return renderQueryValue(v)
}

func renderQueryValue(v any) string {
	switch x := v.(type) {
	case string:
		return x
	case bool:
		if x {
			return "true"
		}
		return "false"
	case float64:
		return strconv.FormatFloat(x, 'g', -1, 64)
	case float32:
		return strconv.FormatFloat(float64(x), 'g', -1, 32)
	case int:
		return strconv.Itoa(x)
	case int64:
		return strconv.FormatInt(x, 10)
	case json.Number:
		return x.String()
	default:
		return fmt.Sprintf("%v", v)
	}
}

// httpToolBridgeRoutes is the tool-name -> endpoint routing table. Ports
// HttpToolBridge.BuildRoutes.
func httpToolBridgeRoutes() map[string]endpointMapping {
	return map[string]endpointMapping{
		"tgn.account.get_profile":                   {"GET", "account/v1/users/{user_id}", bodyNone},
		"tgn.account.update_profile":                {"PATCH", "account/v1/users/me", bodyJSON},
		"tgn.audit.list_events":                     {"GET", "audit/v1/events", bodyQuery},
		"tgn.auth.request_otp":                      {"POST", "auth/v1/otp/request", bodyJSON},
		"tgn.auth.verify_otp":                       {"POST", "auth/v1/otp/verify", bodyJSON},
		"tgn.auth.push_to_app":                      {"POST", "auth/v1/push-to-app", bodyJSON},
		"tgn.bidbaas.list_active_auctions":          {"GET", "bidbaas/v1/auctions/active", bodyQuery},
		"tgn.bidbaas.place_bid":                     {"POST", "bidbaas/v1/auctions/{auction_id}/bids", bodyJSON},
		"tgn.bidbaas.get_auction_details":           {"GET", "bidbaas/v1/auctions/{auction_id}", bodyNone},
		"tgn.billpayment.list_billers":              {"GET", "billpayment/v1/billers", bodyQuery},
		"tgn.billpayment.pay_bill":                  {"POST", "billpayment/v1/payments", bodyJSON},
		"tgn.blockchain.get_transaction":            {"GET", "blockchain/v1/transactions/{tx_hash}", bodyNone},
		"tgn.blockchain.get_address_info":           {"GET", "blockchain/v1/addresses/{address}", bodyNone},
		"tgn.butler.log_interaction":                {"POST", "butler/v1/interactions", bodyJSON},
		"tgn.butler.get_user_context":               {"GET", "butler/v1/users/{user_id}/context", bodyNone},
		"tgn.circleaether.get_node_status":          {"GET", "circleaether/v1/nodes/{device_id}/status", bodyNone},
		"tgn.circleaether.list_nearby_peers":        {"GET", "circleaether/v1/peers/nearby", bodyQuery},
		"tgn.ecommerce.search_products":             {"GET", "ecommerce/v1/products/search", bodyQuery},
		"tgn.ecommerce.get_product":                 {"GET", "ecommerce/v1/products/{product_id}", bodyNone},
		"tgn.electricity.buy_token":                 {"POST", "electricity/v1/tokens", bodyJSON},
		"tgn.electricity.list_recent_purchases":     {"GET", "electricity/v1/purchases", bodyQuery},
		"tgn.geo.get_user_location":                 {"GET", "geo/v1/users/me/location", bodyNone},
		"tgn.geo.geocode_address":                   {"GET", "geo/v1/geocode", bodyQuery},
		"tgn.glocell.list_products":                 {"GET", "glocell/v1/products", bodyQuery},
		"tgn.incentives.get_qi_balance":             {"GET", "incentives/v1/qi/balance", bodyNone},
		"tgn.incentives.list_active_quests":         {"GET", "incentives/v1/quests/active", bodyQuery},
		"tgn.kiffstore.search_items":                {"GET", "kiffstore/v1/items/search", bodyQuery},
		"tgn.ledger.get_account_balance":            {"GET", "ledger/v1/accounts/{account_id}/balance", bodyNone},
		"tgn.ledger.list_entries":                   {"GET", "ledger/v1/accounts/{account_id}/entries", bodyQuery},
		"tgn.localization.translate_text":           {"POST", "localization/v1/translate", bodyJSON},
		"tgn.localization.list_supported_languages": {"GET", "localization/v1/languages", bodyNone},
		"tgn.maps.geocode":                          {"GET", "maps/v1/geocode", bodyQuery},
		"tgn.maps.reverse_geocode":                  {"GET", "maps/v1/reverse-geocode", bodyQuery},
		"tgn.mapsdata.search_pois":                  {"GET", "mapsdata/v1/pois/search", bodyQuery},
		"tgn.media.create_upload_url":               {"POST", "media/v1/uploads", bodyJSON},
		"tgn.media.get_media":                       {"GET", "media/v1/media/{media_id}", bodyNone},
		"tgn.messaging.send_message":                {"POST", "messaging/v1/messages", bodyJSON},
		"tgn.messaging.list_conversations":          {"GET", "messaging/v1/conversations", bodyQuery},
		"tgn.messaging.get_messages":                {"GET", "messaging/v1/conversations/{conversation_id}/messages", bodyQuery},
		"tgn.notification.send_push":                {"POST", "notification/v1/push", bodyJSON},
		"tgn.notification.list_for_user":            {"GET", "notification/v1/notifications", bodyQuery},
		"tgn.opsupport.create_ticket":               {"POST", "opsupport/v1/tickets", bodyJSON},
		"tgn.opsupport.get_system_status":           {"GET", "opsupport/v1/status", bodyNone},
		"tgn.panik.trigger_sos":                     {"POST", "panik/v1/alerts", bodyJSON},
		"tgn.panik.cancel_sos":                      {"POST", "panik/v1/alerts/{alert_id}/cancel", bodyJSON},
		"tgn.payfast.create_payment":                {"POST", "payfast/v1/payments", bodyJSON},
		"tgn.sdpkt.get_balance":                     {"GET", "sdpkt/v1/wallet/balance", bodyNone},
		"tgn.sdpkt.send_payment":                    {"POST", "sdpkt/v1/wallet/transfers", bodyJSON},
		"tgn.sdpkt.get_transactions":                {"GET", "sdpkt/v1/wallet/transactions", bodyQuery},
		"tgn.shhmoney.create_discreet_payment":      {"POST", "shhmoney/v1/payments", bodyJSON},
		"tgn.slepton.list_stories":                  {"GET", "slepton/v1/stories", bodyQuery},
		"tgn.slepton.get_story":                     {"GET", "slepton/v1/stories/{story_id}", bodyNone},
		"tgn.sortedclothing.search_items":           {"GET", "sortedclothing/v1/items/search", bodyQuery},
		"tgn.tagme.create_tag":                      {"POST", "tagme/v1/tags", bodyJSON},
		"tgn.tagme.list_nearby_tags":                {"GET", "tagme/v1/tags/nearby", bodyQuery},
		"tgn.takemehome.search_flights":             {"GET", "takemehome/v1/flights/search", bodyQuery},
		"tgn.takemehome.search_stays":               {"GET", "takemehome/v1/stays/search", bodyQuery},
		"tgn.thehotlist.list_entries":               {"GET", "thehotlist/v1/entries", bodyQuery},
		"tgn.thejobcenter.search_jobs":              {"GET", "thejobcenter/v1/jobs/search", bodyQuery},
		"tgn.thejobcenter.apply":                    {"POST", "thejobcenter/v1/jobs/{job_id}/applications", bodyJSON},
		"tgn.thirdparty.list_integrations":          {"GET", "thirdparty/v1/integrations", bodyNone},
		"tgn.thirdparty.invoke_integration":         {"POST", "thirdparty/v1/integrations/{integration_name}/invoke", bodyJSON},
		"tgn.trustseal.get_status":                  {"GET", "trustseal/v1/status", bodyNone},
		"tgn.trustseal.start_verification":          {"POST", "trustseal/v1/verifications", bodyJSON},
		"tgn.wallet.get_balance":                    {"GET", "wallet/v1/balance", bodyQuery},
		"tgn.wallet.get_transactions":               {"GET", "wallet/v1/transactions", bodyQuery},
		"tgn.whatwewant.list_stories":               {"GET", "whatwewant/v1/stories", bodyQuery},
		"tgn.whatwewant.get_story":                  {"GET", "whatwewant/v1/stories/{story_id}", bodyNone},
		"tgn.wolverine.list_jobs":                   {"GET", "wolverine/v1/jobs", bodyQuery},
	}
}

// ===========================================================================
// ComposioToolBridge — ports ComposioToolBridge.cs
// ===========================================================================

const composioDefaultServerURI = "https://mcp.composio.dev/"

// ComposioToolBridge routes tool calls to a Composio MCP server via JSON-RPC 2.0
// over HTTP. Ports ComposioToolBridge. Construct with NewComposioToolBridge.
// AvailableTools is empty until GetAvailableTools runs discovery.
type ComposioToolBridge struct {
	apiKey    string
	serverURI string // ends with '/'
	doer      ToolHTTPDoer
	tools     []ToolDefinition
}

// NewComposioToolBridge constructs the bridge. serverURI empty uses the default
// (https://mcp.composio.dev/). Returns an error if composioAPIKey is blank or
// doer is nil.
func NewComposioToolBridge(composioAPIKey, serverURI string, doer ToolHTTPDoer) (*ComposioToolBridge, error) {
	if strings.TrimSpace(composioAPIKey) == "" {
		return nil, errors.New("composioApiKey required")
	}
	if doer == nil {
		return nil, errors.New("doer required")
	}
	if serverURI == "" {
		serverURI = composioDefaultServerURI
	}
	if !strings.HasSuffix(serverURI, "/") {
		serverURI += "/"
	}
	return &ComposioToolBridge{
		apiKey:    composioAPIKey,
		serverURI: serverURI,
		doer:      doer,
		tools:     []ToolDefinition{},
	}, nil
}

// AvailableTools returns the discovered tool list (empty until discovery). Ports
// the AvailableTools property.
func (b *ComposioToolBridge) AvailableTools() []ToolDefinition { return b.tools }

// Invoke invokes a tool via a tools/call JSON-RPC 2.0 request. Ports InvokeAsync.
// JSON-RPC/HTTP errors become failure ToolResults; only a doer transport error
// is returned as a Go error.
func (b *ComposioToolBridge) Invoke(ctx context.Context, invocation ToolInvocation) (ToolResult, error) {
	if strings.TrimSpace(invocation.ToolName) == "" {
		return ToolResult{}, errors.New("ToolName must not be null or whitespace.")
	}
	reqBody, _ := json.Marshal(map[string]any{
		"jsonrpc": "2.0",
		"method":  "tools/call",
		"id":      1,
		"params": map[string]any{
			"name":      invocation.ToolName,
			"arguments": invocation.Arguments,
		},
	})
	endpoint := b.serverURI + "tools/" + url.PathEscape(invocation.ToolName) + "/invoke"
	headers := map[string]string{"X-API-Key": b.apiKey, "Content-Type": "application/json", "Accept": "application/json"}

	resp, err := b.doer(ctx, "POST", endpoint, headers, reqBody)
	if err != nil {
		return ToolResult{}, err
	}
	var body map[string]json.RawMessage
	_ = json.Unmarshal(resp.Body, &body)

	if resp.StatusCode < 200 || resp.StatusCode >= 300 {
		httpErr := "HTTP " + strconv.Itoa(resp.StatusCode) + " " + http.StatusText(resp.StatusCode)
		return ToolResultFailure(invocation.ToolName, composioExtractError(body, httpErr)), nil
	}
	if errNode, ok := body["error"]; ok && !isJSONNull(errNode) {
		return ToolResultFailure(invocation.ToolName, composioErrorMessage(errNode)), nil
	}
	if resultNode, ok := body["result"]; ok {
		var parsed any
		_ = json.Unmarshal(resultNode, &parsed)
		return ToolResultOK(invocation.ToolName, parsed), nil
	}
	return ToolResultOK(invocation.ToolName, nil), nil
}

// GetAvailableTools fetches + caches the server's tool list (GET {server}/tools).
// Ports GetAvailableToolsAsync. Any failure yields an empty list (never an
// error), except a doer transport error.
func (b *ComposioToolBridge) GetAvailableTools(ctx context.Context) ([]ToolDefinition, error) {
	endpoint := b.serverURI + "tools"
	headers := map[string]string{"X-API-Key": b.apiKey, "Accept": "application/json"}
	resp, err := b.doer(ctx, "GET", endpoint, headers, nil)
	if err != nil {
		return []ToolDefinition{}, err
	}
	if resp.StatusCode < 200 || resp.StatusCode >= 300 {
		return []ToolDefinition{}, nil
	}
	tools := composioParseToolList(resp.Body)
	b.tools = tools
	return tools, nil
}

func composioParseToolList(raw []byte) []ToolDefinition {
	var root json.RawMessage = raw
	var arr []json.RawMessage
	if json.Unmarshal(root, &arr) != nil {
		// Try { "tools": [...] }.
		var obj map[string]json.RawMessage
		if json.Unmarshal(root, &obj) != nil {
			return []ToolDefinition{}
		}
		toolsRaw, ok := obj["tools"]
		if !ok || json.Unmarshal(toolsRaw, &arr) != nil {
			return []ToolDefinition{}
		}
	}
	result := make([]ToolDefinition, 0, len(arr))
	for _, item := range arr {
		var m map[string]json.RawMessage
		if json.Unmarshal(item, &m) != nil {
			continue
		}
		name := jsonString(m["name"])
		if strings.TrimSpace(name) == "" {
			continue
		}
		desc := jsonString(m["description"])
		parameters := map[string]ToolParameter{}
		required := []string{}
		if schemaRaw, ok := m["inputSchema"]; ok {
			var schema map[string]json.RawMessage
			if json.Unmarshal(schemaRaw, &schema) == nil {
				if propsRaw, ok := schema["properties"]; ok {
					var props map[string]json.RawMessage
					if json.Unmarshal(propsRaw, &props) == nil {
						for propName, propRaw := range props {
							var prop map[string]json.RawMessage
							_ = json.Unmarshal(propRaw, &prop)
							typ := jsonString(prop["type"])
							if typ == "" {
								typ = "string"
							}
							parameters[propName] = ToolParameter{Type: typ, Description: jsonString(prop["description"])}
						}
					}
				}
				if reqRaw, ok := schema["required"]; ok {
					var reqs []string
					if json.Unmarshal(reqRaw, &reqs) == nil {
						for _, r := range reqs {
							if strings.TrimSpace(r) != "" {
								required = append(required, r)
							}
						}
					}
				}
			}
		}
		result = append(result, ToolDefinition{
			Name:               name,
			Description:        desc,
			Parameters:         parameters,
			RequiredParameters: required,
		})
	}
	return result
}

func composioExtractError(body map[string]json.RawMessage, fallback string) string {
	if e, ok := body["error"]; ok {
		return composioErrorMessage(e)
	}
	return fallback
}

func composioErrorMessage(errNode json.RawMessage) string {
	var m map[string]json.RawMessage
	if json.Unmarshal(errNode, &m) == nil {
		if msg, ok := m["message"]; ok {
			if s := jsonString(msg); s != "" {
				return s
			}
		}
	}
	return strings.TrimSpace(string(errNode))
}

func isJSONNull(raw json.RawMessage) bool {
	return strings.TrimSpace(string(raw)) == "null" || len(raw) == 0
}

// Interface guards.
var (
	_ IToolBridge = (*HTTPToolBridge)(nil)
	_ IToolBridge = (*ComposioToolBridge)(nil)
)
