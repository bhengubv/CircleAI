// integration_email_test.go
//
// Verifies the Gmail + MS Graph mail connectors (integration_email.go) over the
// injected FakeCarrierTransport — no real network. Covers Gmail list→per-message
// fetch, header/label/body(base64url) parsing, unread detection, mark-read; and
// MS Graph unread list, search, and mark-read PATCH.

package circleai_test

import (
	"context"
	"encoding/base64"
	"strings"
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

func b64url(s string) string {
	return strings.TrimRight(base64.URLEncoding.EncodeToString([]byte(s)), "=")
}

func TestGmail_ListUnreadFetchesAndParses(t *testing.T) {
	tr := circleai.NewFakeCarrierTransport()
	// 1) list ids
	tr.EnqueueJSON(200, `{"messages":[{"id":"m1"},{"id":"m2"}]}`)
	// 2) m1 full
	tr.EnqueueJSON(200, `{"id":"m1","labelIds":["INBOX","UNREAD"],"internalDate":"1752226200000",
		"payload":{"headers":[
			{"name":"From","value":"alice@x"},
			{"name":"To","value":"me@y, bob@z"},
			{"name":"Subject","value":"Hi"}],
		"body":{"data":"`+b64url("Hello body")+`"}}}`)
	// 3) m2 full (read, multipart text/plain)
	tr.EnqueueJSON(200, `{"id":"m2","labelIds":["INBOX"],"internalDate":"1752226100000",
		"payload":{"headers":[{"name":"From","value":"carol@x"},{"name":"Subject","value":"Later"}],
		"parts":[{"mimeType":"text/html","body":{"data":"`+b64url("<b>x</b>")+`"}},
		         {"mimeType":"text/plain","body":{"data":"`+b64url("plain part")+`"}}]}}`)

	c := mustGmail(t, tr)
	msgs, err := c.ListUnread(context.Background(), 10)
	if err != nil {
		t.Fatalf("list unread: %v", err)
	}
	if len(msgs) != 2 {
		t.Fatalf("expected 2 messages, got %d", len(msgs))
	}
	m1 := msgs[0]
	if m1.MessageID != "m1" || m1.From != "alice@x" || m1.Subject != "Hi" || !m1.Unread ||
		len(m1.To) != 2 || m1.To[0] != "me@y" || m1.To[1] != "bob@z" || m1.BodyText != "Hello body" {
		t.Fatalf("m1 wrong: %+v", m1)
	}
	if !m1.ReceivedUtc.Equal(time.UnixMilli(1752226200000).UTC()) {
		t.Fatalf("m1 received wrong: %v", m1.ReceivedUtc)
	}
	m2 := msgs[1]
	if m2.Unread || m2.BodyText != "plain part" { // prefers text/plain over html
		t.Fatalf("m2 wrong: %+v", m2)
	}
	// First request carries q=is:unread + Bearer.
	req0 := tr.Requests()[0]
	if !strings.Contains(req0.URL, "messages?q=is%3Aunread") || !strings.HasPrefix(req0.Headers["Authorization"], "Bearer ") {
		t.Fatalf("list request wrong: %s hdr=%v", req0.URL, req0.Headers)
	}
}

func TestGmail_SearchValidationAndSkipOnFetchFailure(t *testing.T) {
	c := mustGmail(t, circleai.NewFakeCarrierTransport())
	if _, err := c.Search(context.Background(), "  ", 5); err == nil {
		t.Fatalf("blank query should error")
	}
	if _, err := c.Search(context.Background(), "x", 0); err == nil {
		t.Fatalf("max<=0 should error")
	}
	// A per-message 404 is skipped (not fatal).
	tr := circleai.NewFakeCarrierTransport()
	tr.EnqueueJSON(200, `{"messages":[{"id":"ok"},{"id":"bad"}]}`)
	tr.EnqueueJSON(200, `{"id":"ok","labelIds":["UNREAD"],"internalDate":"0","payload":{"headers":[]}}`)
	tr.EnqueueStatus(404)
	c2 := mustGmail(t, tr)
	msgs, err := c2.Search(context.Background(), "x", 10)
	if err != nil {
		t.Fatalf("search: %v", err)
	}
	if len(msgs) != 1 || msgs[0].MessageID != "ok" {
		t.Fatalf("expected only the ok message, got %+v", msgs)
	}
}

func TestGmail_MarkRead(t *testing.T) {
	tr := circleai.NewFakeCarrierTransport()
	tr.EnqueueStatus(200)
	c := mustGmail(t, tr)
	if err := c.MarkRead(context.Background(), "m1"); err != nil {
		t.Fatalf("mark read: %v", err)
	}
	req, _ := tr.LastRequest()
	if req.Method != "POST" || !strings.Contains(req.URL, "messages/m1/modify") ||
		!strings.Contains(string(req.Body), `"removeLabelIds":["UNREAD"]`) {
		t.Fatalf("mark-read request wrong: %s %s body=%s", req.Method, req.URL, req.Body)
	}
	if err := c.MarkRead(context.Background(), "  "); err == nil {
		t.Fatalf("blank id should error")
	}
}

func TestGmail_ConfigAndAuth(t *testing.T) {
	c, _ := circleai.NewGmailEmailConnector(circleai.NewFakeCarrierTransport(), circleai.GmailOptions{})
	if c.IsConfigured() {
		t.Fatalf("nil token provider should be unconfigured")
	}
	tr := circleai.NewFakeCarrierTransport()
	c2, _ := circleai.NewGmailEmailConnector(tr, circleai.GmailOptions{AccessTokenProvider: fixedToken("")})
	if _, err := c2.ListUnread(context.Background(), 5); err == nil {
		t.Fatalf("blank token should error")
	}
	if len(tr.Requests()) != 0 {
		t.Fatalf("auth failure issued a request")
	}
}

func TestMsGraphMail_ListUnreadAndSearch(t *testing.T) {
	tr := circleai.NewFakeCarrierTransport()
	tr.EnqueueJSON(200, `{"value":[
		{"id":"e1","subject":"Report","isRead":false,
		 "from":{"emailAddress":{"address":"a@x"}},
		 "toRecipients":[{"emailAddress":{"address":"me@y"}}],
		 "receivedDateTime":"2026-07-11T08:00:00Z",
		 "categories":["Work"],"body":{"content":"full body"}}
	]}`)
	c, _ := circleai.NewMsGraphEmailConnector(tr, circleai.MsGraphEmailOptions{AccessTokenProvider: fixedToken("tok")})
	if c.ProviderID() != "ms-graph-mail" || !c.IsConfigured() {
		t.Fatalf("graph mail id/configured wrong")
	}
	msgs, err := c.ListUnread(context.Background(), 5)
	if err != nil {
		t.Fatalf("list unread: %v", err)
	}
	if len(msgs) != 1 {
		t.Fatalf("expected 1, got %d", len(msgs))
	}
	m := msgs[0]
	if m.MessageID != "e1" || m.From != "a@x" || m.To[0] != "me@y" || m.Subject != "Report" ||
		m.BodyText != "full body" || !m.Unread || m.Labels[0] != "Work" {
		t.Fatalf("graph message wrong: %+v", m)
	}
	if !m.ReceivedUtc.Equal(time.Date(2026, 7, 11, 8, 0, 0, 0, time.UTC)) {
		t.Fatalf("received wrong: %v", m.ReceivedUtc)
	}
	req := tr.Requests()[0]
	if !strings.Contains(req.URL, "mailFolders('Inbox')/messages") || !strings.Contains(req.URL, "isRead+eq+false") {
		t.Fatalf("list url wrong: %s", req.URL)
	}

	// Search path uses $search; bodyPreview fallback when no body.content.
	tr2 := circleai.NewFakeCarrierTransport()
	tr2.EnqueueJSON(200, `{"value":[{"id":"s1","isRead":true,"bodyPreview":"preview only","subject":"S"}]}`)
	c2, _ := circleai.NewMsGraphEmailConnector(tr2, circleai.MsGraphEmailOptions{AccessTokenProvider: fixedToken("tok")})
	res, err := c2.Search(context.Background(), "invoice", 5)
	if err != nil {
		t.Fatalf("search: %v", err)
	}
	if len(res) != 1 || res[0].BodyText != "preview only" || res[0].Unread {
		t.Fatalf("search result wrong: %+v", res)
	}
	if !strings.Contains(tr2.Requests()[0].URL, "me/messages?$search=invoice") {
		t.Fatalf("search url wrong: %s", tr2.Requests()[0].URL)
	}
	if _, err := c2.Search(context.Background(), "  ", 5); err == nil {
		t.Fatalf("blank query should error")
	}
}

func TestMsGraphMail_MarkReadPatch(t *testing.T) {
	tr := circleai.NewFakeCarrierTransport()
	tr.EnqueueStatus(200)
	c, _ := circleai.NewMsGraphEmailConnector(tr, circleai.MsGraphEmailOptions{AccessTokenProvider: fixedToken("tok")})
	if err := c.MarkRead(context.Background(), "e1"); err != nil {
		t.Fatalf("mark read: %v", err)
	}
	req, _ := tr.LastRequest()
	if req.Method != "PATCH" || !strings.Contains(req.URL, "me/messages/e1") ||
		!strings.Contains(string(req.Body), `"isRead":true`) {
		t.Fatalf("patch request wrong: %s %s body=%s", req.Method, req.URL, req.Body)
	}
}
