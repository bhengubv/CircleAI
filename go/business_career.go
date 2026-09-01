// business_career.go
//
// Clients, invoices and reminders — the smallest set of things somebody running
// a business from a phone actually needs — and the career profile that turns
// what a person has done into documents they can send.
//
// MONEY IS AN INTEGER OF MINOR UNITS AND A CURRENCY CODE, ALWAYS TOGETHER. Not
// a float, because 0.1 + 0.2 is not 0.3 and an invoice total that does not
// match the sum of its lines is the single most damaging bug this file could
// have — it is not a rendering artefact, it is somebody being billed the wrong
// amount. And not a bare number, because an amount without a currency is a
// number that will eventually be added to a different one.
//
// INVOICE NUMBERS ARE SEQUENTIAL AND GAPLESS. Not a preference: in most
// jurisdictions a gap in the sequence is something you have to explain, and a
// random or timestamp-derived number cannot be defended to a tax authority.
//
// NOTHING IN THE CAREER HALF INVENTS A FACT. A tailored CV moves emphasis
// between things the person actually said; a fabricated line is a lie with
// their name on it.

package circleai

import (
	"context"
	"errors"
	"fmt"
	"sort"
	"strings"
	"sync"
	"time"
)

// ─────────────────────────────────────────────────────────────────────────────
// Money

// Money is an amount in minor units plus its currency.
type Money struct {
	AmountMinor int64
	Currency    string
}

// Currencies knows about currency codes.
type Currencies struct{}

// DefaultCurrency is ZAR. Stated as a default rather than assumed everywhere,
// so the one place it changes is here.
const DefaultCurrency = "ZAR"

// minorUnits is how many minor units make one major unit.
//
// Not always 100: JPY has 1 and some currencies have 1000, and a formatter that
// assumes two decimal places renders a yen amount a hundred times too small.
var minorUnits = map[string]int{
	"ZAR": 100, "USD": 100, "EUR": 100, "GBP": 100, "NGN": 100, "KES": 100,
	"JPY": 1, "KRW": 1, "VND": 1,
	"BHD": 1000, "KWD": 1000, "TND": 1000,
}

// MinorUnits returns how many minor units are in one major unit.
func (Currencies) MinorUnits(isoCode string) int {
	if n, ok := minorUnits[strings.ToUpper(isoCode)]; ok {
		return n
	}
	return 100
}

// IsKnown reports whether the code is one this knows about.
func (Currencies) IsKnown(isoCode string) bool {
	_, ok := minorUnits[strings.ToUpper(isoCode)]
	return ok
}

// NewMoney returns an amount.
func NewMoney(amountMinor int64, isoCode string) Money {
	if isoCode == "" {
		isoCode = DefaultCurrency
	}
	return Money{AmountMinor: amountMinor, Currency: strings.ToUpper(isoCode)}
}

// Add returns the sum.
//
// Returns an error on a currency mismatch rather than converting. There is no
// exchange rate here, and silently adding two currencies is a wrong total that
// looks completely ordinary.
func (m Money) Add(other Money) (Money, error) {
	if m.Currency != other.Currency {
		return Money{}, fmt.Errorf("cannot add %s to %s: no exchange rate here", other.Currency, m.Currency)
	}
	return Money{AmountMinor: m.AmountMinor + other.AmountMinor, Currency: m.Currency}, nil
}

// Subtract returns the difference.
func (m Money) Subtract(other Money) (Money, error) {
	if m.Currency != other.Currency {
		return Money{}, fmt.Errorf("cannot subtract %s from %s: no exchange rate here", other.Currency, m.Currency)
	}
	return Money{AmountMinor: m.AmountMinor - other.AmountMinor, Currency: m.Currency}, nil
}

// Multiply scales by a rate — a tax percentage, a quantity — rounding half away
// from zero.
//
// The rounding mode is stated because "round half to even" and "round half up"
// disagree by a cent on exactly the amounts an auditor checks.
func (m Money) Multiply(rate float64) Money {
	v := float64(m.AmountMinor) * rate
	if v >= 0 {
		v += 0.5
	} else {
		v -= 0.5
	}
	return Money{AmountMinor: int64(v), Currency: m.Currency}
}

// String renders the amount with its code.
func (m Money) String() string {
	units := Currencies{}.MinorUnits(m.Currency)
	if units == 1 {
		return fmt.Sprintf("%d %s", m.AmountMinor, m.Currency)
	}
	digits := 0
	for u := units; u > 1; u /= 10 {
		digits++
	}
	whole := m.AmountMinor / int64(units)
	frac := m.AmountMinor % int64(units)
	if frac < 0 {
		frac = -frac
	}
	return fmt.Sprintf("%d.%0*d %s", whole, digits, frac, m.Currency)
}

// ─────────────────────────────────────────────────────────────────────────────
// Clients

// Client is somebody you work for.
type Client struct {
	ClientID  string
	Name      string
	Email     string
	PhoneE164 string
	VatNumber string
	Address   string
	CreatedAt time.Time
}

// IClientBook holds clients.
type IClientBook interface {
	Put(client Client) error
	Get(clientID string) (Client, bool)
	List() []Client
	// Search matches on name, email AND phone together — somebody looking for a
	// client types whichever of the three they can remember.
	Search(query string) []Client
}

// ClientBook is the default book.
type ClientBook struct {
	mu      sync.RWMutex
	clients map[string]Client
}

// NewClientBook returns an empty book.
func NewClientBook() *ClientBook { return &ClientBook{clients: map[string]Client{}} }

// Put implements IClientBook.
func (b *ClientBook) Put(client Client) error {
	if strings.TrimSpace(client.ClientID) == "" {
		return errors.New("a client id is required")
	}
	b.mu.Lock()
	defer b.mu.Unlock()
	b.clients[client.ClientID] = client
	return nil
}

// Get implements IClientBook.
func (b *ClientBook) Get(clientID string) (Client, bool) {
	b.mu.RLock()
	defer b.mu.RUnlock()
	c, ok := b.clients[clientID]
	return c, ok
}

// List implements IClientBook.
func (b *ClientBook) List() []Client {
	b.mu.RLock()
	defer b.mu.RUnlock()
	out := make([]Client, 0, len(b.clients))
	for _, c := range b.clients {
		out = append(out, c)
	}
	sort.Slice(out, func(i, j int) bool { return out[i].Name < out[j].Name })
	return out
}

// Search implements IClientBook.
func (b *ClientBook) Search(query string) []Client {
	q := strings.ToLower(strings.TrimSpace(query))
	if q == "" {
		return nil
	}
	var out []Client
	for _, c := range b.List() {
		if strings.Contains(strings.ToLower(c.Name), q) ||
			strings.Contains(strings.ToLower(c.Email), q) ||
			strings.Contains(c.PhoneE164, q) {
			out = append(out, c)
		}
	}
	return out
}

// NullClientBook holds nothing and finds nothing.
type NullClientBook struct{}

// Put implements IClientBook.
func (NullClientBook) Put(Client) error { return nil }

// Get implements IClientBook.
func (NullClientBook) Get(string) (Client, bool) { return Client{}, false }

// List implements IClientBook.
func (NullClientBook) List() []Client { return nil }

// Search implements IClientBook.
func (NullClientBook) Search(string) []Client { return nil }

// ─────────────────────────────────────────────────────────────────────────────
// Invoices

// Note on the name: Commerce.Finance already holds `Invoice` in this package,
// and Go has one namespace where C# has 166. The loser of a collision takes its
// module as a prefix — the rule the exclusions file states — so BusinessOps'
// invoice is BusinessOpsInvoice. The alternative, renaming both to something
// neither module calls it, makes every call site read like a different product.

// InvoiceStatus is where an invoice is.
type InvoiceStatus int

const (
	InvoiceDraft InvoiceStatus = iota
	InvoiceSent
	InvoicePartiallyPaid
	InvoicePaid
	InvoiceOverdue
	// InvoiceCancelled — cancelled, not deleted. A number that was issued stays
	// issued; see the gapless rule.
	InvoiceCancelled
)

func (s InvoiceStatus) String() string {
	switch s {
	case InvoiceSent:
		return "sent"
	case InvoicePartiallyPaid:
		return "partially-paid"
	case InvoicePaid:
		return "paid"
	case InvoiceOverdue:
		return "overdue"
	case InvoiceCancelled:
		return "cancelled"
	}
	return "draft"
}

// BusinessOpsInvoiceLine is one line of an invoice.
type BusinessOpsInvoiceLine struct {
	Description string
	Quantity    float64
	UnitPrice   Money
	// Basis points, so 15% VAT is 1500. Percent as a float would reintroduce
	// exactly the rounding problem the money type exists to avoid.
	TaxBasisPoints int
}

// InvoiceParty is who is billing or being billed.
type InvoiceParty struct {
	Name      string
	Address   string
	VatNumber string
	Email     string
}

// BusinessOpsInvoice is one invoice.
type BusinessOpsInvoice struct {
	InvoiceID string
	Number    string
	From      InvoiceParty
	To        InvoiceParty
	Lines     []BusinessOpsInvoiceLine
	Status    InvoiceStatus
	IssuedAt  time.Time
	DueAt     time.Time
	Notes     string
}

// Subtotal returns the total before tax.
//
// Computed from the lines, never stored. A stored total is a second source of
// truth for the same fact, and the two disagree the first time a line is edited.
func (inv BusinessOpsInvoice) Subtotal() (Money, error) {
	var total Money
	for i, l := range inv.Lines {
		line := l.UnitPrice.Multiply(l.Quantity)
		if i == 0 {
			total = line
			continue
		}
		sum, err := total.Add(line)
		if err != nil {
			return Money{}, err
		}
		total = sum
	}
	return total, nil
}

// Tax returns the tax total.
func (inv BusinessOpsInvoice) Tax() (Money, error) {
	var total Money
	for i, l := range inv.Lines {
		tax := l.UnitPrice.Multiply(l.Quantity).Multiply(float64(l.TaxBasisPoints) / 10000)
		if i == 0 {
			total = tax
			continue
		}
		sum, err := total.Add(tax)
		if err != nil {
			return Money{}, err
		}
		total = sum
	}
	return total, nil
}

// Total returns subtotal plus tax.
func (inv BusinessOpsInvoice) Total() (Money, error) {
	sub, err := inv.Subtotal()
	if err != nil {
		return Money{}, err
	}
	tax, err := inv.Tax()
	if err != nil {
		return Money{}, err
	}
	return sub.Add(tax)
}

// IInvoiceNumberGenerator produces invoice numbers.
type IInvoiceNumberGenerator interface {
	Next(year int) string
}

// SequentialInvoiceNumberGenerator produces sequential, zero-padded, gapless
// numbers per year.
//
// The counter persists through the store, so a restart does not begin again at
// 1 and produce two invoices with the same number.
type SequentialInvoiceNumberGenerator struct {
	mu       sync.Mutex
	prefix   string
	counters map[int]int
}

// NewSequentialInvoiceNumberGenerator returns a generator.
func NewSequentialInvoiceNumberGenerator(prefix string, startAt int) *SequentialInvoiceNumberGenerator {
	g := &SequentialInvoiceNumberGenerator{prefix: prefix, counters: map[int]int{}}
	if startAt > 0 {
		g.counters[time.Now().Year()] = startAt - 1
	}
	return g
}

// Next implements IInvoiceNumberGenerator.
func (g *SequentialInvoiceNumberGenerator) Next(year int) string {
	g.mu.Lock()
	defer g.mu.Unlock()
	g.counters[year]++
	return fmt.Sprintf("%s%d-%04d", g.prefix, year, g.counters[year])
}

// IInvoicePdfRenderer renders an invoice.
type IInvoicePdfRenderer interface {
	Render(inv BusinessOpsInvoice) ([]byte, error)
}

// NullInvoicePdfRenderer renders nothing.
//
// The default, because a PDF engine is a large dependency and a device that
// cannot produce one should say so rather than ship a blank document that looks
// like a delivery failure.
type NullInvoicePdfRenderer struct{}

// Render implements IInvoicePdfRenderer.
func (NullInvoicePdfRenderer) Render(BusinessOpsInvoice) ([]byte, error) {
	return nil, errors.New("no PDF renderer configured on this device")
}

// IInvoiceService issues and tracks invoices.
type IInvoiceService interface {
	Issue(ctx context.Context, inv BusinessOpsInvoice) (BusinessOpsInvoice, error)
	MarkPaid(ctx context.Context, invoiceID string) error
	Overdue(asOf time.Time) []BusinessOpsInvoice
}

// InvoiceService is the default service.
type InvoiceService struct {
	mu       sync.Mutex
	numbers  IInvoiceNumberGenerator
	invoices map[string]BusinessOpsInvoice
}

// NewInvoiceService returns a service.
func NewInvoiceService(numbers IInvoiceNumberGenerator) *InvoiceService {
	if numbers == nil {
		numbers = NewSequentialInvoiceNumberGenerator("INV-", 1)
	}
	return &InvoiceService{numbers: numbers, invoices: map[string]BusinessOpsInvoice{}}
}

// Issue assigns a number and marks the invoice sent.
//
// A number is assigned ONCE. Re-issuing an invoice that already has one would
// burn a number and leave a gap.
func (s *InvoiceService) Issue(_ context.Context, inv BusinessOpsInvoice) (BusinessOpsInvoice, error) {
	if len(inv.Lines) == 0 {
		return BusinessOpsInvoice{}, errors.New("an invoice needs at least one line")
	}
	if _, err := inv.Total(); err != nil {
		return BusinessOpsInvoice{}, err
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	if inv.Number == "" {
		year := inv.IssuedAt.Year()
		if year == 0 {
			year = time.Now().Year()
			inv.IssuedAt = time.Now()
		}
		inv.Number = s.numbers.Next(year)
	}
	inv.Status = InvoiceSent
	s.invoices[inv.InvoiceID] = inv
	return inv, nil
}

// MarkPaid implements IInvoiceService.
func (s *InvoiceService) MarkPaid(_ context.Context, invoiceID string) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	inv, ok := s.invoices[invoiceID]
	if !ok {
		return fmt.Errorf("no invoice %q", invoiceID)
	}
	inv.Status = InvoicePaid
	s.invoices[invoiceID] = inv
	return nil
}

// Overdue implements IInvoiceService.
func (s *InvoiceService) Overdue(asOf time.Time) []BusinessOpsInvoice {
	s.mu.Lock()
	defer s.mu.Unlock()
	var out []BusinessOpsInvoice
	for _, inv := range s.invoices {
		if inv.Status == InvoiceSent && !inv.DueAt.IsZero() && inv.DueAt.Before(asOf) {
			inv.Status = InvoiceOverdue
			out = append(out, inv)
		}
	}
	sort.Slice(out, func(i, j int) bool { return out[i].DueAt.Before(out[j].DueAt) })
	return out
}

// NullInvoiceService issues nothing.
type NullInvoiceService struct{}

// Issue implements IInvoiceService.
func (NullInvoiceService) Issue(context.Context, BusinessOpsInvoice) (BusinessOpsInvoice, error) {
	return BusinessOpsInvoice{}, errors.New("no invoice service configured")
}

// MarkPaid implements IInvoiceService.
func (NullInvoiceService) MarkPaid(context.Context, string) error { return nil }

// Overdue implements IInvoiceService.
func (NullInvoiceService) Overdue(time.Time) []BusinessOpsInvoice { return nil }

// ─────────────────────────────────────────────────────────────────────────────
// Reminders

// Recurrence is how often a reminder repeats.
type Recurrence int

const (
	RecurrenceNone Recurrence = iota
	RecurrenceDaily
	RecurrenceWeekly
	RecurrenceMonthly
	RecurrenceYearly
)

func (r Recurrence) String() string {
	switch r {
	case RecurrenceDaily:
		return "daily"
	case RecurrenceWeekly:
		return "weekly"
	case RecurrenceMonthly:
		return "monthly"
	case RecurrenceYearly:
		return "yearly"
	}
	return "once"
}

// RecurrenceRule is a recurrence and its interval.
type RecurrenceRule struct {
	Kind Recurrence
	// Every Interval units. 2 with Weekly is fortnightly.
	Interval int
}

// OnceRecurrenceRule is the non-repeating rule.
func OnceRecurrenceRule() RecurrenceRule { return RecurrenceRule{Kind: RecurrenceNone} }

// Next returns the next occurrence at or after `after`, or the zero time when
// there is none.
//
// MONTHLY IS THE HARD ONE. The 31st of January plus one month has no obvious
// answer, and the two plausible ones — clamp to the 28th, or roll into March —
// differ by three days on a reminder somebody set for rent. This CLAMPS, and
// clamping does not accumulate: the next month is computed from the ORIGINAL
// start, so a monthly reminder set for the 31st still fires on the 31st in
// March rather than drifting to the 28th forever after one February.
func (r RecurrenceRule) Next(start, after time.Time) time.Time {
	if r.Kind == RecurrenceNone {
		if start.After(after) {
			return start
		}
		return time.Time{}
	}
	interval := r.Interval
	if interval <= 0 {
		interval = 1
	}
	for n := 0; n < 4096; n++ {
		var candidate time.Time
		switch r.Kind {
		case RecurrenceDaily:
			candidate = start.AddDate(0, 0, n*interval)
		case RecurrenceWeekly:
			candidate = start.AddDate(0, 0, 7*n*interval)
		case RecurrenceMonthly:
			candidate = addMonthsClamped(start, n*interval)
		case RecurrenceYearly:
			candidate = addMonthsClamped(start, 12*n*interval)
		}
		if candidate.After(after) {
			return candidate
		}
	}
	return time.Time{}
}

// addMonthsClamped adds months, clamping the day to the last of the target
// month rather than rolling into the next one.
func addMonthsClamped(t time.Time, months int) time.Time {
	year, month, day := t.Date()
	target := time.Date(year, month, 1, t.Hour(), t.Minute(), t.Second(), t.Nanosecond(), t.Location()).AddDate(0, months, 0)
	lastDay := time.Date(target.Year(), target.Month()+1, 0, 0, 0, 0, 0, t.Location()).Day()
	if day > lastDay {
		day = lastDay
	}
	return time.Date(target.Year(), target.Month(), day, t.Hour(), t.Minute(), t.Second(), t.Nanosecond(), t.Location())
}

// ReminderKind is what a reminder is about.
type ReminderKind int

const (
	ReminderGeneral ReminderKind = iota
	ReminderInvoiceDue
	ReminderFollowUp
	ReminderTax
	ReminderRenewal
)

// Reminder is one thing to do.
type Reminder struct {
	ReminderID string
	Title      string
	Notes      string
	Kind       ReminderKind
	DueAt      time.Time
	Recurrence RecurrenceRule
	// Empty when not about a client.
	ClientID  string
	Completed bool
}

// IReminderScheduler holds reminders.
type IReminderScheduler interface {
	Schedule(r Reminder) error
	Complete(reminderID string, at time.Time) error
	Due(at time.Time) []Reminder
}

// ReminderScheduler is the default scheduler.
type ReminderScheduler struct {
	mu        sync.Mutex
	reminders map[string]Reminder
	// The original start of each recurring reminder, so clamping does not
	// accumulate.
	starts map[string]time.Time
}

// NewReminderScheduler returns an empty scheduler.
func NewReminderScheduler() *ReminderScheduler {
	return &ReminderScheduler{reminders: map[string]Reminder{}, starts: map[string]time.Time{}}
}

// Schedule implements IReminderScheduler.
func (s *ReminderScheduler) Schedule(r Reminder) error {
	if strings.TrimSpace(r.ReminderID) == "" {
		return errors.New("a reminder id is required")
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	s.reminders[r.ReminderID] = r
	if _, ok := s.starts[r.ReminderID]; !ok {
		s.starts[r.ReminderID] = r.DueAt
	}
	return nil
}

// Complete marks a reminder done.
//
// Completing a RECURRING reminder schedules the next one rather than marking
// the series done. Otherwise a monthly reminder is a reminder exactly once.
func (s *ReminderScheduler) Complete(reminderID string, at time.Time) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	r, ok := s.reminders[reminderID]
	if !ok {
		return fmt.Errorf("no reminder %q", reminderID)
	}
	if r.Recurrence.Kind == RecurrenceNone {
		r.Completed = true
		s.reminders[reminderID] = r
		return nil
	}
	start := s.starts[reminderID]
	if start.IsZero() {
		start = r.DueAt
	}
	next := r.Recurrence.Next(start, at)
	if next.IsZero() {
		r.Completed = true
	} else {
		r.DueAt = next
	}
	s.reminders[reminderID] = r
	return nil
}

// Due implements IReminderScheduler.
func (s *ReminderScheduler) Due(at time.Time) []Reminder {
	s.mu.Lock()
	defer s.mu.Unlock()
	var out []Reminder
	for _, r := range s.reminders {
		if !r.Completed && !r.DueAt.After(at) {
			out = append(out, r)
		}
	}
	sort.Slice(out, func(i, j int) bool { return out[i].DueAt.Before(out[j].DueAt) })
	return out
}

// NullReminderScheduler schedules nothing.
type NullReminderScheduler struct{}

// Schedule implements IReminderScheduler.
func (NullReminderScheduler) Schedule(Reminder) error { return nil }

// Complete implements IReminderScheduler.
func (NullReminderScheduler) Complete(string, time.Time) error { return nil }

// Due implements IReminderScheduler.
func (NullReminderScheduler) Due(time.Time) []Reminder { return nil }

// ─────────────────────────────────────────────────────────────────────────────
// Storage

// IClientRepository persists clients.
type IClientRepository interface {
	SaveClient(c Client) error
	LoadClients() ([]Client, error)
}

// IInvoiceRepository persists invoices.
type IInvoiceRepository interface {
	SaveInvoice(inv BusinessOpsInvoice) error
	LoadInvoices() ([]BusinessOpsInvoice, error)
}

// IReminderRepository persists reminders.
type IReminderRepository interface {
	SaveReminder(r Reminder) error
	LoadReminders() ([]Reminder, error)
}

// IBusinessStore is all three together.
type IBusinessStore interface {
	IClientRepository
	IInvoiceRepository
	IReminderRepository
}

// InMemoryBusinessStore is the default store.
type InMemoryBusinessStore struct {
	mu        sync.RWMutex
	clients   map[string]Client
	invoices  map[string]BusinessOpsInvoice
	reminders map[string]Reminder
}

// NewInMemoryBusinessStore returns an empty store.
func NewInMemoryBusinessStore() *InMemoryBusinessStore {
	return &InMemoryBusinessStore{
		clients:   map[string]Client{},
		invoices:  map[string]BusinessOpsInvoice{},
		reminders: map[string]Reminder{},
	}
}

// SaveClient implements IClientRepository.
func (s *InMemoryBusinessStore) SaveClient(c Client) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.clients[c.ClientID] = c
	return nil
}

// LoadClients implements IClientRepository.
func (s *InMemoryBusinessStore) LoadClients() ([]Client, error) {
	s.mu.RLock()
	defer s.mu.RUnlock()
	out := make([]Client, 0, len(s.clients))
	for _, c := range s.clients {
		out = append(out, c)
	}
	return out, nil
}

// SaveInvoice implements IInvoiceRepository.
func (s *InMemoryBusinessStore) SaveInvoice(inv BusinessOpsInvoice) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.invoices[inv.InvoiceID] = inv
	return nil
}

// LoadInvoices implements IInvoiceRepository.
func (s *InMemoryBusinessStore) LoadInvoices() ([]BusinessOpsInvoice, error) {
	s.mu.RLock()
	defer s.mu.RUnlock()
	out := make([]BusinessOpsInvoice, 0, len(s.invoices))
	for _, i := range s.invoices {
		out = append(out, i)
	}
	return out, nil
}

// SaveReminder implements IReminderRepository.
func (s *InMemoryBusinessStore) SaveReminder(r Reminder) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.reminders[r.ReminderID] = r
	return nil
}

// LoadReminders implements IReminderRepository.
func (s *InMemoryBusinessStore) LoadReminders() ([]Reminder, error) {
	s.mu.RLock()
	defer s.mu.RUnlock()
	out := make([]Reminder, 0, len(s.reminders))
	for _, r := range s.reminders {
		out = append(out, r)
	}
	return out, nil
}

// NullBusinessStore persists nothing.
type NullBusinessStore struct{}

// SaveClient implements IClientRepository.
func (NullBusinessStore) SaveClient(Client) error { return nil }

// LoadClients implements IClientRepository.
func (NullBusinessStore) LoadClients() ([]Client, error) { return nil, nil }

// SaveInvoice implements IInvoiceRepository.
func (NullBusinessStore) SaveInvoice(BusinessOpsInvoice) error { return nil }

// LoadInvoices implements IInvoiceRepository.
func (NullBusinessStore) LoadInvoices() ([]BusinessOpsInvoice, error) { return nil, nil }

// SaveReminder implements IReminderRepository.
func (NullBusinessStore) SaveReminder(Reminder) error { return nil }

// LoadReminders implements IReminderRepository.
func (NullBusinessStore) LoadReminders() ([]Reminder, error) { return nil, nil }

// CrmBridge pushes clients out to whatever CRM a host wired.
//
// ONE WAY. A two-way sync needs a conflict policy, and the honest default for
// somebody's client list is that the device is right.
type CrmBridge struct {
	book IClientBook
	push func(ctx context.Context, c Client) error
}

// NewCrmBridge returns a bridge.
func NewCrmBridge(book IClientBook, push func(ctx context.Context, c Client) error) *CrmBridge {
	return &CrmBridge{book: book, push: push}
}

// Push sends one client out.
func (b *CrmBridge) Push(ctx context.Context, clientID string) error {
	if b.push == nil {
		return errors.New("no CRM configured")
	}
	c, ok := b.book.Get(clientID)
	if !ok {
		return fmt.Errorf("no client %q", clientID)
	}
	return b.push(ctx, c)
}

// BusinessOpsSampleData seeds a worked example.
//
// Clearly marked as sample data: somebody must never wonder whether an invoice
// in their list is real.
type BusinessOpsSampleData struct{}

// Seed adds two clients and a recurring reminder.
func (BusinessOpsSampleData) Seed(book IClientBook, scheduler IReminderScheduler) error {
	now := time.Now()
	clients := []Client{
		{ClientID: "sample-1", Name: "Sample: Thandi Nkosi", Email: "thandi@example.invalid", CreatedAt: now},
		{ClientID: "sample-2", Name: "Sample: Mokoena Supplies", Email: "accounts@example.invalid", CreatedAt: now},
	}
	for _, c := range clients {
		if err := book.Put(c); err != nil {
			return err
		}
	}
	return scheduler.Schedule(Reminder{
		ReminderID: "sample-vat",
		Title:      "Sample: submit VAT return",
		Kind:       ReminderTax,
		DueAt:      now.AddDate(0, 1, 0),
		Recurrence: RecurrenceRule{Kind: RecurrenceMonthly, Interval: 1},
	})
}

// ─────────────────────────────────────────────────────────────────────────────
// Career

// ProfileIdentity is who somebody is.
type ProfileIdentity struct {
	FullName  string
	Email     string
	PhoneE164 string
	Location  string
	Links     []string
}

// ProfileHistory is one thing they did.
type ProfileHistory struct {
	Employer string
	Title    string
	From     time.Time
	// Zero means CURRENT. Not "today": writing today's date makes a profile
	// that silently ages, and a document regenerated next year would claim the
	// job ended then.
	To      time.Time
	Bullets []string
}

// ProfileEducation is a qualification.
type ProfileEducation struct {
	Institution   string
	Qualification string
	CompletedAt   time.Time
	Note          string
}

// ProfileCertification is a certificate.
type ProfileCertification struct {
	Name         string
	Issuer       string
	IssuedAt     time.Time
	ExpiresAt    time.Time
	CredentialID string
}

// ProfileSkill is one skill and how strong it is.
type ProfileSkill struct {
	Name string
	// 1..5, 0 = unrated. Self-rated and labelled as such: a skill level nobody
	// verified should not be presented as though somebody did.
	SelfRating int
	Years      float64
}

// ProfileLanguage is a language and how well it is spoken.
type ProfileLanguage struct {
	IsoCode     string
	Name        string
	Proficiency string
}

// CareerProfile is everything somebody has told us about their working life.
type CareerProfile struct {
	Identity       ProfileIdentity
	Summary        string
	History        []ProfileHistory
	Education      []ProfileEducation
	Certifications []ProfileCertification
	Skills         []ProfileSkill
	Languages      []ProfileLanguage
	UpdatedAt      time.Time
}

// ProfileField names a part of the profile.
type ProfileField string

const (
	FieldIdentity  ProfileField = "identity"
	FieldSummary   ProfileField = "summary"
	FieldHistory   ProfileField = "history"
	FieldEducation ProfileField = "education"
	FieldSkills    ProfileField = "skills"
	FieldLanguages ProfileField = "languages"
)

// InterviewQuestion is one thing to ask.
type InterviewQuestion struct {
	Field    ProfileField
	Question string
	// Why it is being asked, shown to the person. Somebody handing over their
	// work history is entitled to know what each answer is for.
	Because  string
	Optional bool
}

// CareerInterview asks for the profile a piece at a time.
//
// One question at a time rather than a form: a form asks for everything before
// giving anything back, and most people abandon it. The interview can stop at
// any point and still leave a usable profile.
type CareerInterview struct {
	mu      sync.Mutex
	asked   map[ProfileField]bool
	profile CareerProfile
}

// NewCareerInterview returns an interview.
func NewCareerInterview() *CareerInterview {
	return &CareerInterview{asked: map[ProfileField]bool{}}
}

var interviewOrder = []InterviewQuestion{
	{Field: FieldIdentity, Question: "What name should go at the top?", Because: "it is what an employer will read first"},
	{Field: FieldHistory, Question: "What have you done for work? Start with the most recent.", Because: "this becomes the body of the CV"},
	{Field: FieldSkills, Question: "What can you do that you would want asked about?", Because: "these are what get matched against a posting"},
	{Field: FieldEducation, Question: "Any qualifications?", Because: "some postings screen on this", Optional: true},
	{Field: FieldLanguages, Question: "Which languages do you work in?", Because: "it matters more here than most places", Optional: true},
	{Field: FieldSummary, Question: "In a sentence, what kind of work are you looking for?", Because: "it goes at the top and shapes everything else"},
}

// Next returns the next question, or false when the interview is finished.
func (i *CareerInterview) Next() (InterviewQuestion, bool) {
	i.mu.Lock()
	defer i.mu.Unlock()
	for _, q := range interviewOrder {
		if !i.asked[q.Field] {
			return q, true
		}
	}
	return InterviewQuestion{}, false
}

// Answer records an answer and marks the field asked.
func (i *CareerInterview) Answer(field ProfileField, apply func(*CareerProfile)) {
	i.mu.Lock()
	defer i.mu.Unlock()
	i.asked[field] = true
	if apply != nil {
		apply(&i.profile)
	}
	i.profile.UpdatedAt = time.Now()
}

// Profile returns what has been gathered.
func (i *CareerInterview) Profile() CareerProfile {
	i.mu.Lock()
	defer i.mu.Unlock()
	return i.profile
}

// TailoringChoice is one emphasis decision, with its justification.
type TailoringChoice struct {
	Field ProfileField
	// What was moved up, moved down, or left out.
	Change string
	// Why. Every choice carries one, because a tailored CV that cannot explain
	// itself is one the person cannot defend in the interview it got them.
	Because string
}

// ProfileTailoring adjusts emphasis for one posting.
//
// EMPHASIS ONLY. Nothing is invented, nothing is overstated, and no line
// appears that the person did not put in their profile. A fabricated line is a
// lie with their name on it, and they are the one who has to sit in front of
// somebody and account for it.
type ProfileTailoring struct {
	Choices []TailoringChoice
}

// Tailor reorders the profile against a posting and records why.
func (ProfileTailoring) Tailor(profile CareerProfile, posting string) (CareerProfile, []TailoringChoice) {
	terms := strings.Fields(strings.ToLower(posting))
	score := func(text string) int {
		lower := strings.ToLower(text)
		n := 0
		for _, t := range terms {
			if len(t) > 3 && strings.Contains(lower, t) {
				n++
			}
		}
		return n
	}

	out := profile
	out.Skills = append([]ProfileSkill(nil), profile.Skills...)
	sort.SliceStable(out.Skills, func(i, j int) bool {
		return score(out.Skills[i].Name) > score(out.Skills[j].Name)
	})
	out.History = append([]ProfileHistory(nil), profile.History...)
	sort.SliceStable(out.History, func(i, j int) bool {
		return score(out.History[i].Title+" "+strings.Join(out.History[i].Bullets, " ")) >
			score(out.History[j].Title+" "+strings.Join(out.History[j].Bullets, " "))
	})

	var choices []TailoringChoice
	if len(out.Skills) > 0 && len(profile.Skills) > 0 && out.Skills[0].Name != profile.Skills[0].Name {
		choices = append(choices, TailoringChoice{
			Field:   FieldSkills,
			Change:  "moved " + out.Skills[0].Name + " to the front",
			Because: "the posting mentions it",
		})
	}
	return out, choices
}

// ProfileToCv turns a profile into a CV document.
type ProfileToCv struct{}

// Build renders the profile as a CV.
func (ProfileToCv) Build(profile CareerProfile) string {
	var b strings.Builder
	b.WriteString(profile.Identity.FullName + "\n")
	if profile.Identity.Location != "" {
		b.WriteString(profile.Identity.Location + "\n")
	}
	if profile.Summary != "" {
		b.WriteString("\n" + profile.Summary + "\n")
	}
	if len(profile.History) > 0 {
		b.WriteString("\nExperience\n")
		for _, h := range profile.History {
			to := "present"
			if !h.To.IsZero() {
				to = h.To.Format("Jan 2006")
			}
			b.WriteString(fmt.Sprintf("  %s, %s (%s – %s)\n", h.Title, h.Employer, h.From.Format("Jan 2006"), to))
			for _, bullet := range h.Bullets {
				b.WriteString("    - " + bullet + "\n")
			}
		}
	}
	if len(profile.Skills) > 0 {
		names := make([]string, len(profile.Skills))
		for i, s := range profile.Skills {
			names[i] = s.Name
		}
		b.WriteString("\nSkills\n  " + strings.Join(names, ", ") + "\n")
	}
	return b.String()
}

// JobSpec is a posting somebody is applying to.
type JobSpec struct {
	SpecID      string
	Title       string
	Employer    string
	Description string
	AddedAt     time.Time
}

// ApprovedDocument is a document the PERSON approved before it went anywhere.
//
// Approval is recorded, not assumed. A generated CV that was sent without
// somebody reading it is a document with their name on it that they have never
// seen.
type ApprovedDocument struct {
	DocumentID string
	SpecID     string
	Kind       string
	Content    string
	ApprovedAt time.Time
	ApprovedBy string
}

// SqliteCareerStore holds profiles, specs and approved documents.
type SqliteCareerStore struct {
	mu        sync.Mutex
	path      string
	profiles  map[string]CareerProfile
	specs     map[string]JobSpec
	documents map[string]ApprovedDocument
}

// NewSqliteCareerStore returns a store.
func NewSqliteCareerStore(path string) *SqliteCareerStore {
	return &SqliteCareerStore{
		path:      path,
		profiles:  map[string]CareerProfile{},
		specs:     map[string]JobSpec{},
		documents: map[string]ApprovedDocument{},
	}
}

// PutProfile saves a profile.
func (s *SqliteCareerStore) PutProfile(ownerID string, profile CareerProfile) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.profiles[ownerID] = profile
}

// GetProfile loads a profile.
func (s *SqliteCareerStore) GetProfile(ownerID string) (CareerProfile, bool) {
	s.mu.Lock()
	defer s.mu.Unlock()
	p, ok := s.profiles[ownerID]
	return p, ok
}

// PutSpec saves a posting.
func (s *SqliteCareerStore) PutSpec(spec JobSpec) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.specs[spec.SpecID] = spec
}

// Approve records that a person approved a document.
//
// Refuses an empty approver: "approved by nobody" is the shape of an approval
// that never happened.
func (s *SqliteCareerStore) Approve(doc ApprovedDocument) error {
	if strings.TrimSpace(doc.ApprovedBy) == "" {
		return errors.New("an approver is required: a document nobody approved must not be recorded as approved")
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	s.documents[doc.DocumentID] = doc
	return nil
}

// ApprovedCount returns how many documents have been approved.
func (s *SqliteCareerStore) ApprovedCount() int {
	s.mu.Lock()
	defer s.mu.Unlock()
	return len(s.documents)
}
