// integration_email_imap.go
//
// Ports CircleAI.Integration.Email/ImapEmailConnector.cs:
//   ImapOptions          -> ImapOptions
//   ImapEmailConnector   -> ImapEmailConnector (IEmailConnector)
//
// The C# connector drives MailKit's ImapClient over a real TLS socket. IMAP is
// not HTTP, so per the porting rules the socket client is injected behind the
// IMAPClient seam and a deterministic in-memory implementation
// (InMemoryIMAPServer) stands in for a live server — no real network. The
// connector's control flow (connect → authenticate → open folder → search → order
// by UID desc → take max → fetch envelope+flags+body → disconnect; and the
// MarkRead add-Seen path) is reproduced faithfully; MailKit's
// SearchQuery.NotSeen / BodyContains.Or(SubjectContains) and the envelope/flag
// mapping are modelled on the seam.

package circleai

import (
	"context"
	"errors"
	"sort"
	"strconv"
	"strings"
	"time"
)

// ── Injected IMAP seam ──────────────────────────────────────────────────────

// IMAPFolderAccess is the open mode (MailKit FolderAccess).
type IMAPFolderAccess int

const (
	// IMAPReadOnly opens a folder for reading (FolderAccess.ReadOnly).
	IMAPReadOnly IMAPFolderAccess = iota
	// IMAPReadWrite opens a folder for read/write (FolderAccess.ReadWrite).
	IMAPReadWrite
)

// IMAPMessage is a stored message on the seam. UID is MailKit's UniqueId.Id.
// Flags is the set of message-flag names (e.g. "Seen", "Answered"); "Seen"
// governs read/unread. From/To are bare addresses; Date is the envelope date.
type IMAPMessage struct {
	UID      uint32
	From     string
	To       []string
	Subject  string
	Date     time.Time
	TextBody string
	HTMLBody string
	Flags    []string
}

// hasFlag reports whether the message carries flag (case-insensitive).
func (m *IMAPMessage) hasFlag(flag string) bool { return containsFold(m.Flags, flag) }

// IMAPConnection is one opened IMAP connection to a folder — the subset of the
// MailKit surface the connector uses.
type IMAPConnection interface {
	// SearchNotSeen returns the UIDs of messages without the Seen flag
	// (SearchQuery.NotSeen).
	SearchNotSeen() ([]uint32, error)
	// SearchBodyOrSubject returns UIDs whose body OR subject contains query
	// (SearchQuery.BodyContains(q).Or(SubjectContains(q))).
	SearchBodyOrSubject(query string) ([]uint32, error)
	// Fetch returns the messages for uids (envelope + flags + body), in the order
	// given (FetchAsync + per-UID GetMessageAsync).
	Fetch(uids []uint32) ([]IMAPMessage, error)
	// AddSeen sets the Seen flag on uid (AddFlagsAsync(MessageFlags.Seen)).
	AddSeen(uid uint32) error
	// Close disconnects the connection (DisconnectAsync(true)).
	Close() error
}

// IMAPClient opens connections. Ports the ImapClient construction+connect+auth
// lifecycle: Open validates host/credentials + folder and returns a connection.
type IMAPClient interface {
	// Open connects, authenticates with (username,password), and opens folder in
	// the given access mode.
	Open(ctx context.Context, host string, port int, useSSL bool, username, password, folder string, access IMAPFolderAccess) (IMAPConnection, error)
}

// ── ImapOptions / ImapEmailConnector ────────────────────────────────────────

// ImapOptions configures the IMAP connector. Ports ImapOptions. Folder defaults
// to "INBOX" when empty.
type ImapOptions struct {
	Host     string
	Port     int
	UseSSL   bool
	Username string
	Password string
	Folder   string
}

// ImapEmailConnector is a generic IMAP client over the injected IMAPClient seam.
// Ports ImapEmailConnector.
type ImapEmailConnector struct {
	client IMAPClient
	opts   ImapOptions
}

// NewImapEmailConnector constructs the connector over an injected client. client
// is required; an empty Folder defaults to "INBOX" (the C# record default).
func NewImapEmailConnector(client IMAPClient, opts ImapOptions) (*ImapEmailConnector, error) {
	if client == nil {
		return nil, errors.New("client is required")
	}
	if opts.Folder == "" {
		opts.Folder = "INBOX"
	}
	return &ImapEmailConnector{client: client, opts: opts}, nil
}

// ProviderID is "imap".
func (c *ImapEmailConnector) ProviderID() string { return "imap" }

// IsConfigured is true when Host, Username and Password are all non-blank.
func (c *ImapEmailConnector) IsConfigured() bool {
	return stringsTrimSpaceNonEmpty(c.opts.Host) &&
		stringsTrimSpaceNonEmpty(c.opts.Username) &&
		stringsTrimSpaceNonEmpty(c.opts.Password)
}

// ListUnread ports ListUnreadAsync: open ReadOnly, search NotSeen, take the
// highest-UID max, fetch, disconnect.
func (c *ImapEmailConnector) ListUnread(ctx context.Context, max int) ([]EmailMessage, error) {
	if max <= 0 {
		return nil, errors.New("max out of range")
	}
	conn, err := c.client.Open(ctx, c.opts.Host, c.opts.Port, c.opts.UseSSL, c.opts.Username, c.opts.Password, c.opts.Folder, IMAPReadOnly)
	if err != nil {
		return nil, err
	}
	defer conn.Close()
	uids, err := conn.SearchNotSeen()
	if err != nil {
		return nil, err
	}
	return fetchIMAP(conn, sliceHighestUIDs(uids, max))
}

// Search ports SearchAsync: open ReadOnly, search body/subject contains, take the
// highest-UID max, fetch, disconnect.
func (c *ImapEmailConnector) Search(ctx context.Context, query string, max int) ([]EmailMessage, error) {
	if !stringsTrimSpaceNonEmpty(query) {
		return nil, errors.New("query required")
	}
	if max <= 0 {
		return nil, errors.New("max out of range")
	}
	conn, err := c.client.Open(ctx, c.opts.Host, c.opts.Port, c.opts.UseSSL, c.opts.Username, c.opts.Password, c.opts.Folder, IMAPReadOnly)
	if err != nil {
		return nil, err
	}
	defer conn.Close()
	uids, err := conn.SearchBodyOrSubject(query)
	if err != nil {
		return nil, err
	}
	return fetchIMAP(conn, sliceHighestUIDs(uids, max))
}

// MarkRead ports MarkReadAsync: parse the UID, open ReadWrite, add the Seen flag.
func (c *ImapEmailConnector) MarkRead(ctx context.Context, messageID string) error {
	if !stringsTrimSpaceNonEmpty(messageID) {
		return errors.New("messageId required")
	}
	raw, err := strconv.ParseUint(strings.TrimSpace(messageID), 10, 32)
	if err != nil {
		return errors.New("Expected an IMAP UID")
	}
	conn, err := c.client.Open(ctx, c.opts.Host, c.opts.Port, c.opts.UseSSL, c.opts.Username, c.opts.Password, c.opts.Folder, IMAPReadWrite)
	if err != nil {
		return err
	}
	defer conn.Close()
	return conn.AddSeen(uint32(raw))
}

// sliceHighestUIDs orders uids descending by value and takes the first max.
// Ports uids.OrderByDescending(u => u.Id).Take(max).
func sliceHighestUIDs(uids []uint32, max int) []uint32 {
	cp := append([]uint32(nil), uids...)
	sort.Slice(cp, func(i, j int) bool { return cp[i] > cp[j] })
	if len(cp) > max {
		cp = cp[:max]
	}
	return cp
}

// fetchIMAP fetches the given UIDs and maps them to EmailMessages. Ports the
// FetchAsync + envelope/flag/body mapping in the C# FetchAsync helper.
func fetchIMAP(conn IMAPConnection, uids []uint32) ([]EmailMessage, error) {
	if len(uids) == 0 {
		return []EmailMessage{}, nil
	}
	raw, err := conn.Fetch(uids)
	if err != nil {
		return nil, err
	}
	out := make([]EmailMessage, 0, len(raw))
	for i := range raw {
		m := &raw[i]
		body := m.TextBody
		if body == "" {
			body = m.HTMLBody
		}
		received := m.Date
		if received.IsZero() {
			received = nowUTCFunc()
		}
		to := append([]string(nil), m.To...)
		if to == nil {
			to = []string{}
		}
		labels := append([]string(nil), m.Flags...)
		if labels == nil {
			labels = []string{}
		}
		out = append(out, EmailMessage{
			MessageID:   strconv.FormatUint(uint64(m.UID), 10),
			From:        m.From,
			To:          to,
			Subject:     m.Subject,
			BodyText:    body,
			ReceivedUtc: received.UTC(),
			Unread:      !m.hasFlag("Seen"),
			Labels:      labels,
		})
	}
	return out, nil
}

// ── InMemoryIMAPServer — deterministic in-memory IMAP seam ──────────────────

// InMemoryIMAPServer is a deterministic IMAPClient for tests and hermetic hosts.
// It holds folders of messages and enforces the same auth/folder checks a real
// server would, with no sockets. Register credentials + messages, then hand it to
// NewImapEmailConnector.
type InMemoryIMAPServer struct {
	username string
	password string
	folders  map[string][]IMAPMessage // folder name (upper-cased) -> messages
}

// NewInMemoryIMAPServer constructs a server accepting (username,password) and
// seeds the given folders (folder name → messages). "INBOX" is matched
// case-insensitively, mirroring the connector's INBOX special-case.
func NewInMemoryIMAPServer(username, password string, folders map[string][]IMAPMessage) *InMemoryIMAPServer {
	fs := map[string][]IMAPMessage{}
	for name, msgs := range folders {
		fs[strings.ToUpper(name)] = append([]IMAPMessage(nil), msgs...)
	}
	return &InMemoryIMAPServer{username: username, password: password, folders: fs}
}

// Open validates host/credentials/folder and returns a connection bound to the
// folder. Errors on bad credentials or a missing folder (as a live server would).
func (s *InMemoryIMAPServer) Open(_ context.Context, host string, _ int, _ bool, username, password, folder string, access IMAPFolderAccess) (IMAPConnection, error) {
	if !stringsTrimSpaceNonEmpty(host) {
		return nil, errors.New("imap: host required")
	}
	if username != s.username || password != s.password {
		return nil, errors.New("imap: authentication failed")
	}
	key := strings.ToUpper(folder)
	if _, ok := s.folders[key]; !ok {
		return nil, errors.New("imap: no such folder: " + folder)
	}
	return &inMemoryIMAPConn{server: s, folder: key, access: access}, nil
}

// inMemoryIMAPConn is an open connection into one folder of the in-memory server.
type inMemoryIMAPConn struct {
	server *InMemoryIMAPServer
	folder string
	access IMAPFolderAccess
	closed bool
}

func (c *inMemoryIMAPConn) msgs() []IMAPMessage { return c.server.folders[c.folder] }

// SearchNotSeen returns UIDs of messages without the Seen flag.
func (c *inMemoryIMAPConn) SearchNotSeen() ([]uint32, error) {
	if c.closed {
		return nil, errors.New("imap: connection closed")
	}
	var out []uint32
	for i := range c.msgs() {
		m := &c.msgs()[i]
		if !m.hasFlag("Seen") {
			out = append(out, m.UID)
		}
	}
	return out, nil
}

// SearchBodyOrSubject returns UIDs whose body or subject contains query
// (case-insensitive substring, matching IMAP text search semantics).
func (c *inMemoryIMAPConn) SearchBodyOrSubject(query string) ([]uint32, error) {
	if c.closed {
		return nil, errors.New("imap: connection closed")
	}
	q := strings.ToLower(query)
	var out []uint32
	for i := range c.msgs() {
		m := &c.msgs()[i]
		if strings.Contains(strings.ToLower(m.TextBody), q) ||
			strings.Contains(strings.ToLower(m.HTMLBody), q) ||
			strings.Contains(strings.ToLower(m.Subject), q) {
			out = append(out, m.UID)
		}
	}
	return out, nil
}

// Fetch returns copies of the messages for uids, preserving the requested order
// and skipping unknown UIDs.
func (c *inMemoryIMAPConn) Fetch(uids []uint32) ([]IMAPMessage, error) {
	if c.closed {
		return nil, errors.New("imap: connection closed")
	}
	byUID := map[uint32]*IMAPMessage{}
	for i := range c.msgs() {
		byUID[c.msgs()[i].UID] = &c.msgs()[i]
	}
	out := make([]IMAPMessage, 0, len(uids))
	for _, id := range uids {
		if m, ok := byUID[id]; ok {
			cp := *m
			cp.To = append([]string(nil), m.To...)
			cp.Flags = append([]string(nil), m.Flags...)
			out = append(out, cp)
		}
	}
	return out, nil
}

// AddSeen sets the Seen flag on uid. A no-op when the UID is unknown; requires the
// connection to be opened read/write (as MarkRead does).
func (c *inMemoryIMAPConn) AddSeen(uid uint32) error {
	if c.closed {
		return errors.New("imap: connection closed")
	}
	if c.access != IMAPReadWrite {
		return errors.New("imap: folder opened read-only")
	}
	msgs := c.server.folders[c.folder]
	for i := range msgs {
		if msgs[i].UID == uid {
			if !containsFold(msgs[i].Flags, "Seen") {
				msgs[i].Flags = append(msgs[i].Flags, "Seen")
			}
			return nil
		}
	}
	return nil
}

// Close marks the connection disconnected.
func (c *inMemoryIMAPConn) Close() error {
	c.closed = true
	return nil
}

var (
	_ IEmailConnector = (*ImapEmailConnector)(nil)
	_ IMAPClient      = (*InMemoryIMAPServer)(nil)
	_ IMAPConnection  = (*inMemoryIMAPConn)(nil)
)
