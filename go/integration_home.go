// integration_home.go
//
// Ports CircleAI.Integration.HomeAssistant/HomeAssistantConnector.cs:
//   HomeAssistantOptions   -> HomeAssistantOptions
//   HomeAssistantConnector -> HomeAssistantConnector (IHomeAutomationConnector)
//
// The connector speaks the Home Assistant REST API with a long-lived token; the
// live HttpClient is replaced by the injected CarrierHTTP seam per the porting
// rules, so it is deterministic and makes no network calls. Wire details (the
// api/states GET, the api/services/{domain}/{service} POST, the Bearer header, and
// the attribute stringification + friendly-name/domain derivation) are reproduced
// from the C# faithfully.

package circleai

import (
	"context"
	"encoding/json"
	"errors"
	"strings"
)

// HomeAssistantOptions configures the Home Assistant connector. Ports
// HomeAssistantOptions. BaseURL should include a trailing slash.
type HomeAssistantOptions struct {
	BaseURL     string
	AccessToken string
}

// HomeAssistantConnector is a Home Assistant REST client over the injected
// CarrierHTTP. Ports HomeAssistantConnector.
type HomeAssistantConnector struct {
	http    CarrierHTTP
	opts    HomeAssistantOptions
	authHdr string
}

// NewHomeAssistantConnector constructs the connector. http is required (the C#
// ctor throws on null http/opts). The Bearer header is precomputed when a token
// is present, matching the C# constructor.
func NewHomeAssistantConnector(http CarrierHTTP, opts HomeAssistantOptions) (*HomeAssistantConnector, error) {
	if http == nil {
		return nil, errors.New("http is required")
	}
	c := &HomeAssistantConnector{http: http, opts: opts}
	if stringsTrimSpaceNonEmpty(opts.AccessToken) {
		c.authHdr = "Bearer " + opts.AccessToken
	}
	return c, nil
}

// ProviderID is "home-assistant".
func (c *HomeAssistantConnector) ProviderID() string { return "home-assistant" }

// IsConfigured is true when a BaseURL and AccessToken are both present.
func (c *HomeAssistantConnector) IsConfigured() bool {
	return stringsTrimSpaceNonEmpty(c.opts.BaseURL) && stringsTrimSpaceNonEmpty(c.opts.AccessToken)
}

func (c *HomeAssistantConnector) headers(contentType string) map[string]string {
	h := map[string]string{}
	if c.authHdr != "" {
		h["Authorization"] = c.authHdr
	}
	if contentType != "" {
		h["Content-Type"] = contentType
	}
	return h
}

// ListEntities ports ListEntitiesAsync: GET api/states → HaEntity list. A
// non-array body yields an empty list; entities with a blank entity_id are
// skipped; attributes are stringified by JSON kind and friendly_name overrides
// FriendlyName.
func (c *HomeAssistantConnector) ListEntities(_ context.Context) ([]HaEntity, error) {
	resp, err := c.http.Do(&CarrierHTTPRequest{Method: "GET", URL: joinBaseAndPath(c.opts.BaseURL, "api/states"), Headers: c.headers("")})
	if err != nil {
		return nil, err
	}
	if !carrierHTTPStatusOK(resp.StatusCode) {
		return nil, statusError("Home Assistant states", resp.StatusCode)
	}
	// C#: a non-array root yields an empty list (not an error).
	list := []HaEntity{}
	arr, err := parseJSONArray(resp.Body)
	if err != nil {
		return list, nil
	}
	for _, raw := range arr {
		st, ok := asJSONObject(raw)
		if !ok {
			continue
		}
		entityID, _ := tjString(st, "entity_id")
		if entityID == "" {
			continue
		}
		state, _ := tjString(st, "state")
		domain := entityID
		if idx := strings.Index(entityID, "."); idx >= 0 {
			domain = entityID[:idx]
		}
		attrs := map[string]string{}
		friendly := entityID
		if attEl, ok := tjObject(st, "attributes"); ok {
			for name, v := range attEl {
				attrs[name] = tjStringElem(v)
				if name == "friendly_name" {
					if s, ok := v.(string); ok {
						friendly = s
					}
				}
			}
		}
		list = append(list, HaEntity{
			EntityID:     entityID,
			FriendlyName: friendly,
			Domain:       domain,
			State:        state,
			Attributes:   attrs,
		})
	}
	return list, nil
}

// CallService ports CallServiceAsync: POST api/services/{domain}/{service} with
// the data payload (or an empty object). Throws on a blank domain/service.
func (c *HomeAssistantConnector) CallService(_ context.Context, domain, service string, data map[string]interface{}) error {
	if !stringsTrimSpaceNonEmpty(domain) {
		return errors.New("domain required")
	}
	if !stringsTrimSpaceNonEmpty(service) {
		return errors.New("service required")
	}
	payload := data
	if payload == nil {
		payload = map[string]interface{}{}
	}
	body, _ := json.Marshal(payload)
	resp, err := c.http.Do(&CarrierHTTPRequest{
		Method:  "POST",
		URL:     joinBaseAndPath(c.opts.BaseURL, "api/services/"+escapeDataString(domain)+"/"+escapeDataString(service)),
		Headers: c.headers("application/json"),
		Body:    body,
	})
	if err != nil {
		return err
	}
	if !carrierHTTPStatusOK(resp.StatusCode) {
		return statusError("Home Assistant service", resp.StatusCode)
	}
	return nil
}

// TurnOn ports TurnOnAsync: homeassistant.turn_on for entityId.
func (c *HomeAssistantConnector) TurnOn(ctx context.Context, entityID string) error {
	return c.CallService(ctx, "homeassistant", "turn_on", map[string]interface{}{"entity_id": entityID})
}

// TurnOff ports TurnOffAsync: homeassistant.turn_off for entityId.
func (c *HomeAssistantConnector) TurnOff(ctx context.Context, entityID string) error {
	return c.CallService(ctx, "homeassistant", "turn_off", map[string]interface{}{"entity_id": entityID})
}

var _ IHomeAutomationConnector = (*HomeAssistantConnector)(nil)
