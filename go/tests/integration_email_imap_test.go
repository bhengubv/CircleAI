// integration_email_imap_test.go
//
// Verifies the IMAP connector (integration_email_imap.go) over the deterministic
// InMemoryIMAPServer seam — no sockets. Covers unread search + highest-UID
// ordering + cap, body/subject search, envelope/flag/body mapping, and the
// mark-read add-Seen path (which then removes the message from the unread set).

package circleai_test

import (
	"context"
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

func imapServer() *circleai.InMemoryIMAPServer {
	base := time.Date(2026, 7, 11, 8, 0, 0, 0, time.UTC)
	return circleai.NewInMemoryIMAPServer("user", "pass", map[string][]circleai.IMAPMessage{
		"INBOX": {
			{UID: 1, From: "a@x", To: []string{"me@y"}, Subject: "Invoice due", Date: base, TextBody: "please pay the invoice", Flags: []string{"Seen"}},
			{UID: 5, From: "b@x", To: []string{"me@y"}, Subject: "Meeting", Date: base.Add(time.Hour), TextBody: "standup at 9", Flags: nil},
			{UID: 3, From: "c@x", To: []string{"me@y"}, Subject: "Newsletter", Date: base.Add(2 * time.Hour), HTMLBody: "<p>weekly</p>", Flags: nil},
		},
	})
}

func mustImap(t *testing.T, srv circleai.IMAPClient) *circleai.ImapEmailConnector {
	t.Helper()
	c, err := circleai.NewImapEmailConnector(srv, circleai.ImapOptions{
		Host: "imap.example.com", Port: 993, UseSSL: true, Username: "user", Password: "pass",
	})
	if err != nil {
		t.Fatalf("new imap: %v", err)
	}
	return c
}

func TestImap_ConfigAndProviderId(t *testing.T) {
	c := mustImap(t, imapServer())
	if c.ProviderID() != "imap" || !c.IsConfigured() {
		t.Fatalf("imap id/configured wrong")
	}
	bad, _ := circleai.NewImapEmailConnector(imapServer(), circleai.ImapOptions{Host: "", Username: "u", Password: "p"})
	if bad.IsConfigured() {
		t.Fatalf("blank host should be unconfigured")
	}
}

func TestImap_ListUnreadOrdersByUidDescAndCaps(t *testing.T) {
	c := mustImap(t, imapServer())
	msgs, err := c.ListUnread(context.Background(), 10)
	if err != nil {
		t.Fatalf("list unread: %v", err)
	}
	// UID 1 is Seen -> excluded; unread are {5,3}; ordered desc by UID.
	if len(msgs) != 2 || msgs[0].MessageID != "5" || msgs[1].MessageID != "3" {
		t.Fatalf("unread order wrong: %+v", msgs)
	}
	if !msgs[0].Unread || msgs[0].Subject != "Meeting" || msgs[0].From != "b@x" || msgs[0].BodyText != "standup at 9" {
		t.Fatalf("uid5 message wrong: %+v", msgs[0])
	}
	// HTML fallback body when TextBody empty.
	if msgs[1].BodyText != "<p>weekly</p>" {
		t.Fatalf("uid3 body fallback wrong: %q", msgs[1].BodyText)
	}
	// Cap to 1 keeps the highest UID.
	capped, _ := c.ListUnread(context.Background(), 1)
	if len(capped) != 1 || capped[0].MessageID != "5" {
		t.Fatalf("cap wrong: %+v", capped)
	}
}

func TestImap_SearchBodyOrSubject(t *testing.T) {
	c := mustImap(t, imapServer())
	// "invoice" matches uid1 body/subject (case-insensitive).
	res, err := c.Search(context.Background(), "invoice", 10)
	if err != nil {
		t.Fatalf("search: %v", err)
	}
	if len(res) != 1 || res[0].MessageID != "1" {
		t.Fatalf("search invoice wrong: %+v", res)
	}
	// uid1 is Seen -> Unread false.
	if res[0].Unread {
		t.Fatalf("seen message should not be unread")
	}
	if _, err := c.Search(context.Background(), "  ", 5); err == nil {
		t.Fatalf("blank query should error")
	}
}

func TestImap_MarkReadAddsSeen(t *testing.T) {
	srv := imapServer()
	c := mustImap(t, srv)
	// UID 5 starts unread.
	before, _ := c.ListUnread(context.Background(), 10)
	if len(before) != 2 {
		t.Fatalf("precondition: expected 2 unread, got %d", len(before))
	}
	if err := c.MarkRead(context.Background(), "5"); err != nil {
		t.Fatalf("mark read: %v", err)
	}
	after, _ := c.ListUnread(context.Background(), 10)
	if len(after) != 1 || after[0].MessageID != "3" {
		t.Fatalf("after mark-read unread wrong: %+v", after)
	}
	// Non-numeric id -> error.
	if err := c.MarkRead(context.Background(), "abc"); err == nil {
		t.Fatalf("non-numeric uid should error")
	}
	if err := c.MarkRead(context.Background(), "  "); err == nil {
		t.Fatalf("blank uid should error")
	}
}

func TestImap_AuthFailure(t *testing.T) {
	srv := circleai.NewInMemoryIMAPServer("user", "right", map[string][]circleai.IMAPMessage{"INBOX": nil})
	c, _ := circleai.NewImapEmailConnector(srv, circleai.ImapOptions{Host: "h", Username: "user", Password: "wrong"})
	if _, err := c.ListUnread(context.Background(), 5); err == nil {
		t.Fatalf("bad password should error")
	}
}

func TestImap_FolderDefaultsToInbox(t *testing.T) {
	// Empty Folder resolves to INBOX (matched case-insensitively).
	c := mustImap(t, imapServer())
	if _, err := c.ListUnread(context.Background(), 5); err != nil {
		t.Fatalf("default INBOX folder should resolve: %v", err)
	}
}
