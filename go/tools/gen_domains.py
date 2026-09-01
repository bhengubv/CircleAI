"""Generates go/domain_adapters.go and go/wiring.go.

Two shapes repeat across the C# solution often enough that writing them out by
hand forty-five times is how one of them ends up subtly different and nobody
notices:

  XDomainContext      one system-prompt snippet, stating what the domain is and
                      what the assistant must not do in it
  XCompanionAdapter   a decorator over a companion session that prefixes the
                      snippet and forwards

  XServiceCollectionExtensions  the DI registration surface. Go has no DI
                      container, so what survives the port is the part that
                      matters: WHAT THE DEFAULTS ARE. Every one of them is the
                      safe end — a null store, a disabled runner, a refusing
                      gate — and having them in one generated place is what
                      makes that reviewable in a sitting.

The table is the source, exactly as the C# is generated from the same list of
decisions. A domain added later inherits the shape for free.
"""
import io, os

# (module, go prefix, one-line domain statement, the refusal that belongs in the
#  snippet — empty when the domain has none)
# Domains whose DomainContext already exists elsewhere in the package. The
# generator emits only the adapter for these: two snippets for one domain is one
# edit away from an assistant that says different things depending on which
# constructor a host reached for.
CONTEXT_ALREADY_DEFINED = {"Safety", "SafetyChild"}

# Media's context is a struct with a method rather than a package-level value,
# so the adapter constructs it rather than reaching for a singleton.
CONTEXT_IS_TYPE = {"Media"}

DOMAINS = [
    ("Accessibility", "Accessibility",
     "You are helping somebody use this device the way it works for them.",
     "Never describe a person's needs as a deficiency, and never record a diagnosis."),
    ("Agriculture", "Agriculture",
     "You are helping with growing, livestock and land.",
     "Do not give chemical dosing advice; name the product and say to read its label."),
    ("Beauty", "Beauty", "You are helping with grooming and appearance.",
     "Never comment on a person's appearance unless they asked."),
    ("Business", "Business", "You are helping run a business.", ""),
    ("Civic", "Civic", "You are helping somebody deal with public bodies and representatives.",
     "Report who holds an office and how to reach them. Never advise how to vote."),
    ("Commerce", "Commerce", "You are helping with buying and selling.",
     "Never complete a purchase; prepare it and hand it back."),
    ("Commerce.Accounting", "CommerceAccounting", "You are helping keep the books.",
     "You are not an accountant and this is not filed advice."),
    ("Commerce.Finance", "CommerceFinance", "You are helping with payments and cash flow.",
     "Never move money."),
    ("Commerce.Integration.PayFast", "CommerceIntegrationPayFast",
     "You are helping with PayFast payment integration.",
     "Never handle a live merchant key; sandbox unless told otherwise."),
    ("Commerce.Integration.Xero", "CommerceIntegrationXero",
     "You are helping with Xero accounting integration.", ""),
    ("Community", "Community", "You are helping with community organising and volunteering.", ""),
    ("Construction", "Construction", "You are helping with building work, costs and scheduling.",
     "Never give structural or safety-critical advice; name the professional to ask."),
    ("Creative", "Creative", "You are helping make something.", ""),
    ("Education", "Education", "You are helping somebody learn or teach.",
     "Explain rather than answer when the point is that somebody learns it."),
    ("Elderly", "Elderly", "You are helping care for somebody older.",
     "Never record that a medication was taken; only that a reminder was acknowledged."),
    ("Energy", "Energy", "You are helping with power use, meters and tariffs.", ""),
    ("Faith", "Faith", "You are helping with matters of faith and practice.",
     "Never adjudicate between traditions, and never repeat what was shared in confidence."),
    ("Family", "Family", "You are helping a household run.", ""),
    ("Fitness", "Fitness", "You are helping with training.",
     "You are not a physiotherapist; stop at pain and name who to ask."),
    ("Food", "Food", "You are helping with meals, shopping and cooking.",
     "Take an allergy at face value and never work around one."),
    ("Gaming", "Gaming", "You are helping with games.", ""),
    ("HR", "HR", "You are helping with people and employment matters.",
     "Never draft a decision about a named person's employment. A policy can be "
     "summarised; who to let go cannot be recommended."),
    ("Healthcare", "Healthcare", "You are helping somebody navigate care.",
     "You do not diagnose and you do not dose. Say what to ask and who to ask."),
    ("Home", "Home", "You are helping run and maintain a home.", ""),
    ("Hospitality", "Hospitality", "You are helping run a place where people stay.",
     "A guest's details are theirs; never repeat them to another guest."),
    ("Kids", "Kids", "You are talking with a child.",
     "Refuse rather than soften. A softened answer to a child is still an answer."),
    ("Legal", "Legal", "You are helping somebody understand a legal situation.",
     "This is not legal advice, and say so in the answer rather than only in the prompt. "
     "A disclaimer the model was told about but did not say is a disclaimer nobody received."),
    ("Logistics", "Logistics", "You are helping move things.", ""),
    ("Media", "Media", "You are helping with images and clips.",
     "Describe what you would make before making it."),
    ("Parenting", "Parenting", "You are helping somebody parent.",
     "Offer options, not verdicts. You are not in the room."),
    ("Personal.Finance", "PersonalFinance", "You are helping somebody with their own money.",
     "Explain and total. Never move money, and never recommend an investment."),
    ("Personal.Health", "PersonalHealth", "You are helping somebody track their own health.",
     "Readings are readings. You do not diagnose."),
    ("Personal.Mental", "PersonalMental", "You are helping somebody with how they are doing.",
     "Some things are not yours to handle. Say so plainly and name who is."),
    ("Pets", "Pets", "You are helping look after an animal.",
     "Never advise on medication or dosage; name the vet."),
    ("RealEstate", "RealEstate", "You are helping with property.", ""),
    ("Relationships", "Relationships", "You are helping somebody keep in touch.",
     "Never draft a message as though the person wrote it themselves."),
    ("Retail", "Retail", "You are helping run a shop.", ""),
    ("Safety", "Safety", "You are helping somebody stay safe.",
     "Some things are not yours to handle. Escalate and say to whom."),
    ("Safety.Child", "SafetyChild", "You are helping keep a child safe.",
     "Refuse rather than soften, and escalate to an adult who can act."),
    ("Social", "Social", "You are helping with what somebody shares publicly.",
     "Never post. Prepare and hand it back."),
    ("Sports", "Sports", "You are helping with sport and training.", ""),
    ("Tourism", "Tourism", "You are helping somebody see a place.", ""),
    ("Travel", "Travel", "You are helping somebody travel.",
     "Never book anything; prepare it and hand it back."),
    ("Wearable", "Wearable", "You are answering on a device with a very small screen.",
     "Shorten the answer itself. Truncating a long one leaves a sentence that stops."),
]

# Modules whose C# ServiceCollectionExtensions carry defaults worth stating.
# (go type name, one-line what-it-wires, the defaults sentence)
WIRING = [
    ("CircleAIRuntimeServiceCollectionExtensions", "the runtime",
     "a backend selector that picks CPU unless a host says otherwise"),
    ("MemoryServiceCollectionExtensions", "the memory store",
     "an in-memory store and an append-only log with no file behind it"),
    ("PluginsServiceCollectionExtensions", "the plugin host",
     "a root resolver pointing at the project, and every plugin disabled"),
    ("CloudFallbackServiceCollectionExtensions", "the cloud fallback chain",
     "an empty chain: no provider is configured, so nothing leaves the device"),
    ("McpServiceCollectionExtensions", "the MCP endpoints",
     "the endpoints registered but no tools imported"),
    ("MultiplayerServiceCollectionExtensions", "multiplayer hosting",
     "a per-session peer identity, never the device's AetherTag"),
    ("NeuronServiceCollectionExtensions", "the resident-model manager",
     "one slot and a RAM ceiling the host must set"),
    ("RealtimeServiceCollectionExtensions", "the realtime contracts",
     "no transport, so the local voice loop runs"),
    ("RealtimeCloudServiceCollectionExtensions", "the realtime cloud services",
     "a null transport factory: the local loop runs unless a provider is wired"),
    ("SpeechCloudServiceCollectionExtensions", "the cloud speech services",
     "nothing configured, so audio stays on the device"),
    ("VisionCloudServiceCollectionExtensions", "the cloud image generators",
     "nothing configured"),
    ("TwilioServiceCollectionExtensions", "the Twilio carrier",
     "absent unless credentials are supplied, so no test can place a real call"),
    ("TelnyxServiceCollectionExtensions", "the Telnyx carrier",
     "absent unless credentials are supplied"),
    ("PlivoServiceCollectionExtensions", "the Plivo carrier",
     "absent unless credentials are supplied"),
]

HEADER = '''// domain_adapters.go
//
// GENERATED by go/tools/gen_domains.py. Do not edit by hand; edit the table.
//
// Two things per vertical: a DOMAIN CONTEXT, which is one system-prompt snippet
// saying what the domain is and what the assistant must not do in it, and a
// COMPANION ADAPTER, which decorates an existing session with that snippet.
//
// THE ADAPTER DECORATES, IT DOES NOT REPLACE. It adds no capability the session
// did not already have, so a domain cannot quietly acquire the ability to send
// mail or spend money by being a domain.
//
// The refusals are the point. Most of these snippets exist to say what the
// assistant will NOT do in that domain — diagnose, dose, move money, post,
// book, or decide somebody's employment — and a domain adapter with no refusal
// is one where nothing was thought through rather than one where nothing was
// needed.

package circleai

// DomainContext is one domain's system-prompt snippet.
type DomainContext interface {
	DomainID() string
	SystemPromptSnippet() string
}

// staticDomainContext carries a snippet that is declared elsewhere.
type staticDomainContext struct {
	id      string
	snippet string
}

// DomainID implements DomainContext.
func (c staticDomainContext) DomainID() string { return c.id }

// SystemPromptSnippet implements DomainContext.
func (c staticDomainContext) SystemPromptSnippet() string { return c.snippet }

// DomainCompanionAdapter decorates a companion session with a domain snippet.
//
// `inner` is BORROWED and never owned: the session outlives any one domain
// adapter, and a domain that closed it would take the assistant down with it.
type DomainCompanionAdapter struct {
	inner   any
	context DomainContext
}

// NewDomainCompanionAdapter returns an adapter over a session.
func NewDomainCompanionAdapter(inner any, context DomainContext) *DomainCompanionAdapter {
	return &DomainCompanionAdapter{inner: inner, context: context}
}

// DomainID returns which domain this adapter is for.
func (a *DomainCompanionAdapter) DomainID() string {
	if a.context == nil {
		return ""
	}
	return a.context.DomainID()
}

// Enrich prefixes the domain snippet to a message.
func (a *DomainCompanionAdapter) Enrich(message string) string {
	if a.context == nil {
		return message
	}
	return a.context.SystemPromptSnippet() + "\\n\\n" + message
}

// Inner returns the wrapped session.
func (a *DomainCompanionAdapter) Inner() any { return a.inner }

'''

WIRING_HEADER = '''// wiring.go
//
// GENERATED by go/tools/gen_domains.py. Do not edit by hand; edit the table.
//
// The C# registers each module's implementations against a ServiceCollection.
// Go has no DI container, so what survives the port is the part that actually
// matters: WHAT THE DEFAULTS ARE.
//
// Every one of them is the safe end — a null store, a disabled runner, an empty
// provider chain, a carrier that dials nobody. Having them in one generated
// place is what makes that reviewable in a sitting, rather than spread across
// twenty-five files where one of them quietly defaults the other way.

package circleai

// Wiring is a module's default wiring, stated rather than registered.
type Wiring interface {
	// Wires says what this module registers.
	Wires() string
	// Defaults says what a host gets when it configures nothing. Always the
	// safe end.
	Defaults() string
}

'''


def emit():
    out = [HEADER]
    for module, prefix, statement, refusal in DOMAINS:
        if prefix in CONTEXT_IS_TYPE:
            out.append('''// %(p)sCompanionAdapter is a companion session scoped to the %(m)s domain.
//
// The snippet is declared elsewhere in this package as a struct with a method,
// and is used directly rather than restated.
type %(p)sCompanionAdapter struct{ *DomainCompanionAdapter }

// New%(p)sCompanionAdapter wraps a session for the %(m)s domain.
func New%(p)sCompanionAdapter(inner any) *%(p)sCompanionAdapter {
	return &%(p)sCompanionAdapter{NewDomainCompanionAdapter(inner,
		staticDomainContext{id: %(mq)s, snippet: %(p)sDomainContext{}.SystemPromptSnippet()})}
}

''' % {"p": prefix, "m": module, "mq": '"%s"' % module})
            continue
        snippet = statement
        if refusal:
            snippet += " " + refusal
        if prefix in CONTEXT_ALREADY_DEFINED:
            out.append('''// %(p)sCompanionAdapter is a companion session scoped to the %(m)s domain.
//
// The snippet itself is declared elsewhere in this package and is reached
// through it rather than restated: two snippets for one domain is one edit away
// from an assistant that says different things depending on which constructor a
// host reached for.
type %(p)sCompanionAdapter struct{ *DomainCompanionAdapter }

// New%(p)sCompanionAdapter wraps a session for the %(m)s domain.
func New%(p)sCompanionAdapter(inner any) *%(p)sCompanionAdapter {
	return &%(p)sCompanionAdapter{NewDomainCompanionAdapter(inner,
		staticDomainContext{id: %(mq)s, snippet: %(p)sDomainContext.SystemPromptSnippet()})}
}

''' % {"p": prefix, "m": module, "mq": '"%s"' % module})
            continue
        out.append('''// %(p)sDomainContext is the %(m)s domain's prompt snippet.
type %(p)sDomainContext struct{}

// DomainID implements DomainContext.
func (%(p)sDomainContext) DomainID() string { return %(mq)s }

// SystemPromptSnippet implements DomainContext.
func (%(p)sDomainContext) SystemPromptSnippet() string {
	return %(sq)s
}

// %(p)sCompanionAdapter is a companion session scoped to the %(m)s domain.
type %(p)sCompanionAdapter struct{ *DomainCompanionAdapter }

// New%(p)sCompanionAdapter wraps a session for the %(m)s domain.
func New%(p)sCompanionAdapter(inner any) *%(p)sCompanionAdapter {
	return &%(p)sCompanionAdapter{NewDomainCompanionAdapter(inner, %(p)sDomainContext{})}
}

''' % {"p": prefix, "m": module, "mq": '"%s"' % module, "sq": '"%s"' % snippet.replace('"', '\\"')})

    io.open("go/domain_adapters.go", "w", encoding="utf-8", newline="\n").write("".join(out))
    print("wrote go/domain_adapters.go — %d verticals" % len(DOMAINS))

    w = [WIRING_HEADER]
    for name, wires, defaults in WIRING:
        w.append('''// %(n)s states the defaults for %(w)s.
type %(n)s struct{}

// Wires implements Wiring.
func (%(n)s) Wires() string { return %(wq)s }

// Defaults implements Wiring.
func (%(n)s) Defaults() string { return %(dq)s }

''' % {"n": name, "w": wires, "wq": '"%s"' % wires, "dq": '"%s"' % defaults.replace('"', '\\"')})

    io.open("go/wiring.go", "w", encoding="utf-8", newline="\n").write("".join(w))
    print("wrote go/wiring.go — %d modules" % len(WIRING))


emit()
