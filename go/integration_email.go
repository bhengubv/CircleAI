// integration_email.go
//
// Ports CircleAI.Integration.Email:
//   GmailOptions / GmailEmailConnector       -> GmailOptions / GmailEmailConnector
//   MsGraphEmailOptions / MsGraphEmailConnector -> MsGraphEmailOptions / MsGraphEmailConnector
//
// (ImapOptions / ImapEmailConnector are ported in integration_email_imap.go over
// an injected IMAP client seam, as IMAP is not HTTP.)
//
// Both HTTP connectors are IEmailConnectors speaking a real REST API; the live
// HttpClient is replaced by the injected CarrierHTTP seam per the porting rules,
// so they are deterministic and make no network calls. Wire details (paths, query
// params, verbs, JSON bodies, base64url body decode, header extraction) are
// reproduced from the C# faithfully.

package circleai

import (
	"context"
	"encoding/base64"
	"encoding/json"
	"errors"
	"strings"
	"time"
)

// ── Gmail ───────────────────────────────────────────────────────────────────

// gmailBaseURI is the Gmail API v1 "users/me" base.
const gmailBaseURI = "https://gmail.googleapis.com/gmail/v1/users/me/"

// GmailOptions configures the Gmail connector. Ports GmailOptions.
type GmailOptions struct {
	AccessTokenProvider AccessTokenProvider
}

// GmailEmailConnector is a Gmail API v1 client over the injected CarrierHTTP.
// Ports GmailEmailConnector.
type GmailEmailConnector struct {
	http CarrierHTTP
	opts GmailOptions
	base string
}

// NewGmailEmailConnector constructs the connector. http is required.
func NewGmailEmailConnector(http CarrierHTTP, opts GmailOptions) (*GmailEmailConnector, error) {
	if http == nil {
		return nil, errors.New("http is required")
	}
	return &GmailEmailConnector{http: http, opts: opts, base: gmailBaseURI}, nil
}

// ProviderID is "gmail".
func (c *GmailEmailConnector) ProviderID() string { return "gmail" }

// IsConfigured is true when an AccessTokenProvider is set.
func (c *GmailEmailConnector) IsConfigured() bool { return c.opts.AccessTokenProvider != nil }

func (c *GmailEmailConnector) ensureAuth(ctx context.Context) (string, error) {
	if c.opts.AccessTokenProvider == nil {
		return "", errors.New("Gmail access token unavailable; refresh OAuth.")
	}
	token, err := c.opts.AccessTokenProvider(ctx)
	if err != nil {
		return "", err
	}
	if !stringsTrimSpaceNonEmpty(token) {
		return "", errors.New("Gmail access token unavailable; refresh OAuth.")
	}
	return "Bearer " + token, nil
}

// ListUnread ports ListUnreadAsync: SearchAsync("is:unread", max).
func (c *GmailEmailConnector) ListUnread(ctx context.Context, max int) ([]EmailMessage, error) {
	return c.Search(ctx, "is:unread", max)
}

// Search ports SearchAsync: list message ids for q, then fetch each full message.
func (c *GmailEmailConnector) Search(ctx context.Context, query string, max int) ([]EmailMessage, error) {
	if !stringsTrimSpaceNonEmpty(query) {
		return nil, errors.New("query required")
	}
	if max <= 0 {
		return nil, errors.New("max out of range")
	}
	auth, err := c.ensureAuth(ctx)
	if err != nil {
		return nil, err
	}
	listPath := "messages?q=" + escapeDataString(query) + "&maxResults=" + itoaSmall(minInt(max, 100))
	listResp, err := c.http.Do(&CarrierHTTPRequest{Method: "GET", URL: joinBaseAndPath(c.base, listPath), Headers: map[string]string{"Authorization": auth}})
	if err != nil {
		return nil, err
	}
	if !carrierHTTPStatusOK(listResp.StatusCode) {
		return nil, statusError("Gmail messages list", listResp.StatusCode)
	}
	listRoot, err := parseJSONObject(listResp.Body)
	if err != nil {
		return nil, err
	}
	var ids []string
	if msgs, ok := tjArray(listRoot, "messages"); ok {
		for _, m := range msgs {
			if mm, ok := asJSONObject(m); ok {
				if id, ok := tjString(mm, "id"); ok {
					ids = append(ids, id)
				}
			}
		}
	}

	result := make([]EmailMessage, 0, len(ids))
	for _, id := range ids {
		getResp, err := c.http.Do(&CarrierHTTPRequest{Method: "GET", URL: joinBaseAndPath(c.base, "messages/"+escapeDataString(id)+"?format=full"), Headers: map[string]string{"Authorization": auth}})
		if err != nil {
			return nil, err
		}
		if !carrierHTTPStatusOK(getResp.StatusCode) {
			continue // C#: skip messages that don't fetch cleanly.
		}
		msgRoot, err := parseJSONObject(getResp.Body)
		if err != nil {
			return nil, err
		}
		result = append(result, parseGmailMessage(msgRoot))
	}
	return result, nil
}

// MarkRead ports MarkReadAsync: POST messages/{id}/modify removing the UNREAD
// label.
func (c *GmailEmailConnector) MarkRead(ctx context.Context, messageID string) error {
	if !stringsTrimSpaceNonEmpty(messageID) {
		return errors.New("messageId required")
	}
	auth, err := c.ensureAuth(ctx)
	if err != nil {
		return err
	}
	body, _ := json.Marshal(map[string]interface{}{"removeLabelIds": []string{"UNREAD"}})
	resp, err := c.http.Do(&CarrierHTTPRequest{
		Method:  "POST",
		URL:     joinBaseAndPath(c.base, "messages/"+escapeDataString(messageID)+"/modify"),
		Headers: map[string]string{"Authorization": auth, "Content-Type": "application/json"},
		Body:    body,
	})
	if err != nil {
		return err
	}
	if !carrierHTTPStatusOK(resp.StatusCode) {
		return statusError("Gmail modify", resp.StatusCode)
	}
	return nil
}

// parseGmailMessage ports ParseGmailMessage: labels, unread flag, header map,
// body extraction, and the internalDate → ReceivedUtc conversion.
func parseGmailMessage(msg map[string]interface{}) EmailMessage {
	id, _ := tjString(msg, "id")
	labels := []string{}
	if labs, ok := tjArray(msg, "labelIds"); ok {
		for _, l := range labs {
			if s, ok := l.(string); ok {
				labels = append(labels, s)
			}
		}
	}
	unread := containsFold(labels, "UNREAD")

	headers := map[string]string{}
	if payload, ok := tjObject(msg, "payload"); ok {
		if hs, ok := tjArray(payload, "headers"); ok {
			for _, h := range hs {
				if hm, ok := asJSONObject(h); ok {
					name, nok := tjString(hm, "name")
					val, vok := tjString(hm, "value")
					if nok && vok {
						headers[strings.ToLower(name)] = val
					}
				}
			}
		}
	}
	var bodyText string
	if payload, ok := msg["payload"]; ok {
		bodyText = gmailExtractBody(payload)
	}
	var receivedMs int64
	if s, ok := tjString(msg, "internalDate"); ok {
		receivedMs = parseInt64(s)
	}
	to := []string{}
	if t, ok := headerFold(headers, "to"); ok {
		for _, part := range strings.Split(t, ",") {
			p := strings.TrimSpace(part)
			if p != "" {
				to = append(to, p)
			}
		}
	}
	from, _ := headerFold(headers, "from")
	subject, _ := headerFold(headers, "subject")
	return EmailMessage{
		MessageID:   id,
		From:        from,
		To:          to,
		Subject:     subject,
		BodyText:    bodyText,
		ReceivedUtc: unixMillisUTC(receivedMs),
		Unread:      unread,
		Labels:      labels,
	}
}

// gmailExtractBody ports ExtractBody: prefer this node's body.data (base64url);
// else recurse into a text/plain part; else the first non-empty part.
func gmailExtractBody(node interface{}) string {
	payload, ok := asJSONObject(node)
	if !ok {
		return ""
	}
	if body, ok := tjObject(payload, "body"); ok {
		if data, ok := tjString(body, "data"); ok {
			return decodeBase64URL(data)
		}
	}
	if parts, ok := tjArray(payload, "parts"); ok {
		for _, part := range parts {
			if pm, ok := asJSONObject(part); ok {
				if mime, ok := tjString(pm, "mimeType"); ok && strings.EqualFold(mime, "text/plain") {
					return gmailExtractBody(part)
				}
			}
		}
		for _, part := range parts {
			if content := gmailExtractBody(part); content != "" {
				return content
			}
		}
	}
	return ""
}

// decodeBase64URL ports DecodeBase64Url: base64url → UTF-8, padding restored,
// invalid input → "".
func decodeBase64URL(s string) string {
	if s == "" {
		return ""
	}
	s = strings.ReplaceAll(s, "-", "+")
	s = strings.ReplaceAll(s, "_", "/")
	if pad := len(s) % 4; pad > 0 {
		s += strings.Repeat("=", 4-pad)
	}
	b, err := base64.StdEncoding.DecodeString(s)
	if err != nil {
		return ""
	}
	return string(b)
}

// ── Microsoft Graph mail ────────────────────────────────────────────────────

// MsGraphEmailOptions configures the MS Graph mail connector. Ports
// MsGraphEmailOptions.
type MsGraphEmailOptions struct {
	AccessTokenProvider AccessTokenProvider
}

// MsGraphEmailConnector is a Microsoft Graph v1.0 mail client over the injected
// CarrierHTTP. Ports MsGraphEmailConnector.
type MsGraphEmailConnector struct {
	http CarrierHTTP
	opts MsGraphEmailOptions
	base string
}

// NewMsGraphEmailConnector constructs the connector. http is required.
func NewMsGraphEmailConnector(http CarrierHTTP, opts MsGraphEmailOptions) (*MsGraphEmailConnector, error) {
	if http == nil {
		return nil, errors.New("http is required")
	}
	return &MsGraphEmailConnector{http: http, opts: opts, base: msGraphBaseURI}, nil
}

// ProviderID is "ms-graph-mail".
func (c *MsGraphEmailConnector) ProviderID() string { return "ms-graph-mail" }

// IsConfigured is true when an AccessTokenProvider is set.
func (c *MsGraphEmailConnector) IsConfigured() bool { return c.opts.AccessTokenProvider != nil }

func (c *MsGraphEmailConnector) ensureAuth(ctx context.Context) (string, error) {
	if c.opts.AccessTokenProvider == nil {
		return "", errors.New("Microsoft Graph access token unavailable; refresh OAuth.")
	}
	token, err := c.opts.AccessTokenProvider(ctx)
	if err != nil {
		return "", err
	}
	if !stringsTrimSpaceNonEmpty(token) {
		return "", errors.New("Microsoft Graph access token unavailable; refresh OAuth.")
	}
	return "Bearer " + token, nil
}

// ListUnread ports ListUnreadAsync: GET the Inbox unread messages.
func (c *MsGraphEmailConnector) ListUnread(ctx context.Context, max int) ([]EmailMessage, error) {
	auth, err := c.ensureAuth(ctx)
	if err != nil {
		return nil, err
	}
	path := "me/mailFolders('Inbox')/messages?$filter=isRead+eq+false&$top=" + itoaSmall(minInt(max, 50)) + "&$orderby=receivedDateTime+desc"
	resp, err := c.http.Do(&CarrierHTTPRequest{Method: "GET", URL: joinBaseAndPath(c.base, path), Headers: map[string]string{"Authorization": auth}})
	if err != nil {
		return nil, err
	}
	if !carrierHTTPStatusOK(resp.StatusCode) {
		return nil, statusError("MS Graph messages", resp.StatusCode)
	}
	root, err := parseJSONObject(resp.Body)
	if err != nil {
		return nil, err
	}
	return msGraphReadMessages(root), nil
}

// Search ports SearchAsync: GET me/messages?$search={query}.
func (c *MsGraphEmailConnector) Search(ctx context.Context, query string, max int) ([]EmailMessage, error) {
	if !stringsTrimSpaceNonEmpty(query) {
		return nil, errors.New("query required")
	}
	auth, err := c.ensureAuth(ctx)
	if err != nil {
		return nil, err
	}
	path := "me/messages?$search=" + escapeDataString(query) + "&$top=" + itoaSmall(minInt(max, 50))
	resp, err := c.http.Do(&CarrierHTTPRequest{Method: "GET", URL: joinBaseAndPath(c.base, path), Headers: map[string]string{"Authorization": auth}})
	if err != nil {
		return nil, err
	}
	if !carrierHTTPStatusOK(resp.StatusCode) {
		return nil, statusError("MS Graph search", resp.StatusCode)
	}
	root, err := parseJSONObject(resp.Body)
	if err != nil {
		return nil, err
	}
	return msGraphReadMessages(root), nil
}

// MarkRead ports MarkReadAsync: PATCH me/messages/{id} with isRead=true.
func (c *MsGraphEmailConnector) MarkRead(ctx context.Context, messageID string) error {
	if !stringsTrimSpaceNonEmpty(messageID) {
		return errors.New("messageId required")
	}
	auth, err := c.ensureAuth(ctx)
	if err != nil {
		return err
	}
	body, _ := json.Marshal(map[string]interface{}{"isRead": true})
	resp, err := c.http.Do(&CarrierHTTPRequest{
		Method:  "PATCH",
		URL:     joinBaseAndPath(c.base, "me/messages/"+escapeDataString(messageID)),
		Headers: map[string]string{"Authorization": auth, "Content-Type": "application/json"},
		Body:    body,
	})
	if err != nil {
		return err
	}
	if !carrierHTTPStatusOK(resp.StatusCode) {
		return statusError("MS Graph patch", resp.StatusCode)
	}
	return nil
}

// msGraphReadMessages ports ReadMessages: the "value" array → EmailMessages.
func msGraphReadMessages(root map[string]interface{}) []EmailMessage {
	list := []EmailMessage{}
	arr, ok := tjArray(root, "value")
	if !ok {
		return list
	}
	for _, it := range arr {
		m, ok := asJSONObject(it)
		if !ok {
			continue
		}
		to := []string{}
		if rcpts, ok := tjArray(m, "toRecipients"); ok {
			for _, r := range rcpts {
				if rm, ok := asJSONObject(r); ok {
					if ea, ok := tjObject(rm, "emailAddress"); ok {
						if addr, ok := tjString(ea, "address"); ok {
							to = append(to, addr)
						}
					}
				}
			}
		}
		fromAddr := ""
		if fr, ok := tjObject(m, "from"); ok {
			if fea, ok := tjObject(fr, "emailAddress"); ok {
				if fa, ok := tjString(fea, "address"); ok {
					fromAddr = fa
				}
			}
		}
		var received time.Time
		if rd, ok := tjString(m, "receivedDateTime"); ok {
			received = parseDateTimeOffsetUTC(rd)
		}
		labels := []string{}
		if cats, ok := tjArray(m, "categories"); ok {
			for _, c := range cats {
				if s, ok := c.(string); ok {
					labels = append(labels, s)
				}
			}
		}
		body := ""
		if b, ok := tjObject(m, "body"); ok {
			if bc, ok := tjString(b, "content"); ok {
				body = bc
			}
		} else if bp, ok := tjString(m, "bodyPreview"); ok {
			body = bp
		}
		id, _ := tjString(m, "id")
		subject, _ := tjString(m, "subject")
		// Unread = isRead is present and JSON false.
		unread := false
		if v, ok := m["isRead"]; ok {
			if b, ok := v.(bool); ok && !b {
				unread = true
			}
		}
		list = append(list, EmailMessage{
			MessageID:   id,
			From:        fromAddr,
			To:          to,
			Subject:     subject,
			BodyText:    body,
			ReceivedUtc: received,
			Unread:      unread,
			Labels:      labels,
		})
	}
	return list
}

var (
	_ IEmailConnector = (*GmailEmailConnector)(nil)
	_ IEmailConnector = (*MsGraphEmailConnector)(nil)
)
