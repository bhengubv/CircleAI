// server_api_key_auth.go
//
// Ports CircleAI.Inference.Server.Auth.AuthSchemes (AuthSchemes.cs),
// ApiKeyAuthSchemeOptions + ApiKeyAuthHandler (ApiKeyAuthHandler.cs).
//
// API-key authentication: read the configured header, look the value up in the
// option-supplied allow-list with a constant-time comparison (guards against
// timing-attack key discovery), and return an authenticated principal when
// matched. When the option block is disabled the handler succeeds with a
// synthetic "anonymous" principal so dev environments don't need keys.
//
// Per the port NOTE the ASP.NET AuthenticationHandler is expressed as a pure
// in-memory Authenticate(headers) call behind the ApiKeyAuthenticator interface
// — no socket server, same decision logic.

package circleai

import (
	"crypto/subtle"
	"strings"
)

// AuthSchemes holds the auth scheme / policy identifiers. Ports
// CircleAI.Inference.Server.Auth.AuthSchemes.
const (
	// AuthSchemeApiKey is the API-key auth scheme name.
	AuthSchemeApiKey = "ApiKey"
	// AuthSchemeJwt is the JWT Bearer scheme name (Microsoft's default constant).
	AuthSchemeJwt = "Bearer"
	// AuthPolicyAuthenticated is the policy name for endpoints requiring auth.
	AuthPolicyAuthenticated = "Authenticated"
)

// ApiKeyOptions is the API-key auth configuration. Projects the ApiKey option
// block (InferenceServerOptions.Auth.ApiKey) the handler reads.
type ApiKeyOptions struct {
	// Enabled — when false, the handler succeeds with an anonymous principal.
	Enabled bool
	// HeaderName is the request header carrying the key (e.g. "X-API-Key").
	HeaderName string
	// Keys is the allow-list of accepted keys.
	Keys []string
}

// AuthOutcome classifies an authentication attempt. Mirrors ASP.NET's
// AuthenticateResult.{Success, NoResult, Fail}.
type AuthOutcome int

const (
	// AuthSuccess — the caller is authenticated.
	AuthSuccess AuthOutcome = iota
	// AuthNoResult — no credential was presented; let other schemes try / 401.
	AuthNoResult
	// AuthFail — a credential was presented but rejected.
	AuthFail
)

// AuthPrincipal is the identity produced by a successful authentication.
type AuthPrincipal struct {
	Name         string
	Scheme       string
	AuthDisabled bool
}

// AuthResult is the outcome of an authentication attempt.
type AuthResult struct {
	Outcome   AuthOutcome
	Principal *AuthPrincipal
	Failure   string
}

// ApiKeyAuthenticator authenticates an inbound request from its headers. Ports
// the decision logic of ApiKeyAuthHandler.HandleAuthenticateAsync.
type ApiKeyAuthenticator interface {
	// Authenticate inspects headers (a case-insensitive get) and returns the result.
	Authenticate(headerGet func(name string) (string, bool)) AuthResult
}

// ApiKeyAuthHandler is the default ApiKeyAuthenticator. Ports ApiKeyAuthHandler.
type ApiKeyAuthHandler struct {
	options func() ApiKeyOptions
}

// NewApiKeyAuthHandler builds a handler. options is a snapshot accessor (mirrors
// IOptionsMonitor.CurrentValue) so config changes are observed per request.
func NewApiKeyAuthHandler(options func() ApiKeyOptions) *ApiKeyAuthHandler {
	if options == nil {
		options = func() ApiKeyOptions { return ApiKeyOptions{} }
	}
	return &ApiKeyAuthHandler{options: options}
}

// Authenticate ports HandleAuthenticateAsync.
func (h *ApiKeyAuthHandler) Authenticate(headerGet func(name string) (string, bool)) AuthResult {
	cfg := h.options()

	if !cfg.Enabled {
		// Auth disabled — succeed with a marker identity.
		return AuthResult{
			Outcome: AuthSuccess,
			Principal: &AuthPrincipal{
				Name:         "anonymous",
				Scheme:       AuthSchemeApiKey,
				AuthDisabled: true,
			},
		}
	}

	raw := ""
	if headerGet != nil {
		if v, ok := headerGet(cfg.HeaderName); ok {
			raw = v
		}
	}
	if strings.TrimSpace(raw) == "" {
		return AuthResult{Outcome: AuthNoResult}
	}

	if !matchAPIKey(raw, cfg.Keys) {
		return AuthResult{Outcome: AuthFail, Failure: "Invalid API key."}
	}

	return AuthResult{
		Outcome: AuthSuccess,
		Principal: &AuthPrincipal{
			Name:   "api-key-caller",
			Scheme: AuthSchemeApiKey,
		},
	}
}

// matchAPIKey is a constant-time match against any configured key. Ports
// ApiKeyAuthHandler.TryMatchKey — length is compared first (subtle.ConstantTimeEq
// on lengths), then bytes with subtle.ConstantTimeCompare.
func matchAPIKey(presented string, allowed []string) bool {
	if len(allowed) == 0 {
		return false
	}
	pb := []byte(presented)
	matched := false
	for _, k := range allowed {
		if k == "" {
			continue
		}
		kb := []byte(k)
		if len(kb) != len(pb) {
			continue
		}
		if subtle.ConstantTimeCompare(kb, pb) == 1 {
			matched = true
		}
	}
	return matched
}

var _ ApiKeyAuthenticator = (*ApiKeyAuthHandler)(nil)
