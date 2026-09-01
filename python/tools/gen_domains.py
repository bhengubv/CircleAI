"""Generates the vertical companion adapters and the wiring modules.

GENERATED BECAUSE THEY ARE THE SAME SHAPE, not because they are unimportant.
Forty-odd verticals each need a domain context, a refusal set and an adapter,
and hand-writing them means forty chances to leave one out of a later change.
The shape is stated once, here, and the differences live in the table.

WHAT IS PER-VERTICAL AND REAL: the topics it will speak to, the things it must
refuse, and the wording of the refusal. Healthcare declines to diagnose, legal
declines to advise, finance declines to recommend an investment - each in its
own words, because a generic "I cannot help with that" tells nobody why and
teaches them nothing about what the assistant is for.
"""

from __future__ import annotations

import io
import os

#: name -> (module suffix, what it is for, topics, what it refuses)
#:
#: The refusals are the point. Each is a thing the vertical must not do however
#: it is asked, and each is worded for the person asking rather than as a policy
#: identifier.
DOMAINS: dict[str, tuple[str, str, tuple[str, ...], tuple[str, str]]] = {
    "Accessibility": ("accessibility", "making a device usable",
        ("screen reading", "captions", "switch access", "magnification"),
        ("assess a disability", "I can help with settings and tools, but what "
         "you need assessed is for a professional")),
    "Agriculture": ("agriculture", "growing things",
        ("planting windows", "soil", "rainfall", "pests", "market days"),
        ("recommend a pesticide dose", "the label and your extension officer "
         "have the dose for your crop and your soil")),
    "Beauty": ("beauty", "hair, skin and appointments",
        ("routines", "products", "appointments", "prices"),
        ("treat a skin condition", "a rash or a reaction needs someone who can "
         "look at it")),
    "Business": ("business", "running a small business",
        ("quotes", "invoices", "customers", "stock", "pricing"),
        ("give tax advice", "the numbers I can do; what you owe is for someone "
         "registered to say")),
    "Civic": ("civic", "dealing with public services",
        ("documents", "queues", "offices", "deadlines", "rights"),
        ("tell you how to vote", "I will find you the facts and the dates; the "
         "choice is not mine to influence")),
    "Commerce": ("commerce", "buying and selling",
        ("listings", "orders", "delivery", "returns"),
        ("move money", "I can prepare it; sending it is yours to do")),
    "Commerce.Accounting": ("commerce_accounting", "books and reconciliation",
        ("ledgers", "reconciliation", "VAT", "statements"),
        ("sign off accounts", "reconciling I can do; signing is an accountant's")),
    "Commerce.Finance": ("commerce_finance", "business finance",
        ("cash flow", "credit terms", "invoicing", "forecasts"),
        ("recommend an investment", "I am not licensed to advise, and a wrong "
         "answer here costs you money")),
    "Commerce.Integration.PayFast": ("commerce_payfast", "PayFast payments",
        ("payment links", "settlement", "refunds"),
        ("take a card number", "never type a card number to me - use the "
         "payment page")),
    "Commerce.Integration.Xero": ("commerce_xero", "Xero bookkeeping",
        ("invoices", "contacts", "bank feeds"),
        ("change your books without asking", "I will draft it and show you "
         "before anything is posted")),
    "Community": ("community", "neighbourhood life",
        ("notices", "meetings", "shared work", "local services"),
        ("share a neighbour's details", "that is theirs to give out, not mine")),
    "Construction": ("construction", "building work",
        ("materials", "quantities", "schedules", "site notes"),
        ("sign off a structure", "quantities I can do; an engineer signs")),
    "Creative": ("creative", "making things",
        ("drafts", "edits", "briefs", "portfolios"),
        ("pass someone else's work off as yours", "I will help you make your "
         "own")),
    "Education": ("education", "learning and teaching",
        ("explanations", "practice", "marking", "timetables"),
        ("write an assessment you will submit as your own", "I will teach it "
         "and check your attempt")),
    "Elderly": ("elderly", "living well later",
        ("reminders", "appointments", "check-ins", "company"),
        ("change a medicine", "the dose on the box is the dose; anything else "
         "is for the pharmacist")),
    "Energy": ("energy", "power and its cost",
        ("load shedding", "tariffs", "usage", "solar"),
        ("work on a live board", "the numbers I can do; the wiring is an "
         "electrician's")),
    "Faith": ("faith", "religious life",
        ("readings", "times", "calendars", "community"),
        ("tell you what to believe", "I can find the text and the times; the "
         "rest is not mine")),
    "Family": ("family", "running a household",
        ("schedules", "chores", "budgets", "occasions"),
        ("read a family member's messages", "their phone is theirs")),
    "Fitness": ("fitness", "training",
        ("sessions", "progress", "recovery", "routes"),
        ("push you through pain", "pain is information; stop and ask someone "
         "who can look at it")),
    "Food": ("food", "cooking and eating",
        ("recipes", "substitutions", "shopping", "batch cooking"),
        ("advise on a medical diet", "an allergy or a condition needs a "
         "dietitian, not me")),
    "Gaming": ("gaming", "games",
        ("progress", "strategies", "sessions", "friends"),
        ("spend money in a game", "I will tell you what it costs; buying is "
         "yours")),
    "HR": ("hr", "people at work",
        ("leave", "policies", "onboarding", "reviews"),
        ("decide a disciplinary case", "I can lay out the policy; a decision "
         "about a person is for people")),
    "Healthcare": ("healthcare", "health information",
        ("appointments", "medicines", "clinics", "records"),
        ("diagnose you", "I can tell you what is written down and where to go; "
         "what is wrong with you is for a clinician")),
    "Home": ("home", "the house itself",
        ("maintenance", "repairs", "suppliers", "inventory"),
        ("work on gas or a live circuit", "that is a licensed trade for good "
         "reason")),
    "Hospitality": ("hospitality", "guests and venues",
        ("bookings", "menus", "shifts", "reviews"),
        ("post a review as a guest", "a review has to be theirs")),
    "Kids": ("kids", "younger users",
        ("homework", "stories", "games", "questions"),
        ("talk about anything a grown-up should be there for", "let us find "
         "someone you trust to help with that one")),
    "Legal": ("legal", "the law",
        ("documents", "deadlines", "rights", "where to go"),
        ("give legal advice", "I can explain what a document says and what a "
         "date means; advice is for someone admitted to practise")),
    "Logistics": ("logistics", "moving things",
        ("routes", "loads", "deliveries", "tracking"),
        ("falsify a delivery record", "the record has to say what happened")),
    "Parenting": ("parenting", "raising children",
        ("routines", "milestones", "school", "sleep"),
        ("assess a child's development", "every child differs; a clinic can "
         "tell you what I cannot")),
    "Personal.Finance": ("personal_finance", "personal money",
        ("budgets", "bills", "savings", "debt"),
        ("recommend where to invest", "I will do your arithmetic; I am not "
         "licensed to tell you where to put your money")),
    "Personal.Health": ("personal_health", "your own health record",
        ("measurements", "appointments", "medicines", "history"),
        ("interpret a test result", "the number I can store; what it means is "
         "for the person who ordered it")),
    "Personal.Mental": ("personal_mental", "how you are doing",
        ("mood", "sleep", "journalling", "support"),
        ("stand in for care", "if things are bad right now, please talk to "
         "someone - I will help you find who")),
    "Pets": ("pets", "animals in the house",
        ("feeding", "vaccinations", "vets", "routines"),
        ("dose an animal", "weights and doses are the vet's")),
    "RealEstate": ("real_estate", "property",
        ("listings", "viewings", "documents", "prices"),
        ("value a property", "I can show you what sold nearby; a valuation is "
         "signed by someone qualified")),
    "Relationships": ("relationships", "people you are close to",
        ("dates", "occasions", "messages", "plans"),
        ("read or write to somebody as you without asking", "I will draft it "
         "and you send it")),
    "Retail": ("retail", "a shop",
        ("stock", "prices", "customers", "shifts"),
        ("change a price without asking", "I will suggest it; you set it")),
    "Safety": ("safety", "staying safe",
        ("routes", "check-ins", "contacts", "alerts"),
        ("replace emergency services", "if this is an emergency, call - I will "
         "find the number")),
    "Safety.Child": ("safety_child", "children's safety",
        ("check-ins", "boundaries", "contacts"),
        ("track a child without them knowing", "anyone being located should "
         "know they are")),
    "Social": ("social", "keeping in touch",
        ("posts", "replies", "groups", "events"),
        ("post as you without asking", "I will draft it; posting is yours")),
    "Sports": ("sports", "playing and following",
        ("fixtures", "results", "training", "teams"),
        ("place a bet", "not something I do")),
    "Tourism": ("tourism", "showing people around",
        ("places", "times", "prices", "getting there"),
        ("guarantee a border or visa outcome", "I can tell you the requirement; "
         "the decision is the official's")),
    "Travel": ("travel", "going somewhere",
        ("routes", "bookings", "documents", "costs"),
        ("book and pay", "I will get it ready; paying is yours")),
    "Wearable": ("wearable", "what a worn device notices",
        ("steps", "heart rate", "sleep", "reminders"),
        ("diagnose from a sensor", "a wrist is not a clinic")),
}

#: The wiring modules. Each is a registration point, nothing more.
WIRING: dict[str, tuple[str, str]] = {
    "AetherNet": ("aethernet_wiring", "ServiceCollectionExtensions"),
    "CodeAgent": ("code_agent_wiring", "ServiceCollectionExtensions"),
    "Companion": ("companion_wiring", "ServiceCollectionExtensions"),
    "Hosting": ("hosting_wiring", "ServiceCollectionExtensions"),
    "Hosting.Mcp": ("mcp_wiring", "McpServiceCollectionExtensions"),
    "Hosting.Multiplayer": ("multiplayer_wiring", "MultiplayerServiceCollectionExtensions"),
    "Memory": ("memory_wiring", "MemoryServiceCollectionExtensions"),
    "Mesh": ("mesh_wiring", "ServiceCollectionExtensions"),
    "Realtime": ("realtime_wiring", "RealtimeServiceCollectionExtensions"),
    "Runtime": ("runtime_wiring", "CircleAIRuntimeServiceCollectionExtensions"),
    "Security.AetherNet": ("security_aethernet_wiring", "ServiceCollectionExtensions"),
    "Speech.Cloud": ("speech_cloud_wiring", "SpeechCloudServiceCollectionExtensions"),
    "Telephony": ("telephony_wiring", "TelephonyServiceCollectionExtensions"),
    "Telephony.Plivo": ("plivo_wiring", "PlivoServiceCollectionExtensions"),
    "Telephony.Telnyx": ("telnyx_wiring", "TelnyxServiceCollectionExtensions"),
    "Telephony.Twilio": ("twilio_wiring", "TwilioServiceCollectionExtensions"),
    "Vision.Cloud": ("vision_cloud_wiring", "VisionCloudServiceCollectionExtensions"),
}

HEADER = '''"""{title}

GENERATED by tools/gen_domains.py. The shape is stated once there; what differs
between verticals - the topics, and above all the refusals - lives in the table
rather than in forty hand-copied files.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from typing import Callable, Sequence


'''

ADAPTER = '''@dataclass(frozen=True)
class {name}DomainContext:
    """What the companion can do about {purpose} on this device."""

    #: What it will speak to.
    topics: tuple[str, ...] = {topics}
    #: The one thing it will not do, however it is asked. Held as a pair so the
    #: refusal can be SHOWN with its reason - a refusal without a reason teaches
    #: nobody what the assistant is for.
    refuses: str = {refuses!r}
    because: str = {because!r}
    #: Whether a host has wired anything real behind this. False means the
    #: adapter answers from what it knows and says so, rather than pretending.
    is_connected: bool = False

    def describe(self) -> str:
        return (
            "this can help with " + ", ".join(self.topics)
            + f"; it will not {{self.refuses}} - {{self.because}}"
        )


class {name}CompanionAdapter:
    """{purpose_sentence}

    THE REFUSAL IS CHECKED BEFORE THE HANDLER RUNS, not after. Producing an
    answer and then deciding it should not have been given has already produced
    it, and on a device that streams, already shown it.
    """

    def __init__(
        self,
        context: "{name}DomainContext | None" = None,
        handle: "Callable[[str], str] | None" = None,
        refuses_when: "Callable[[str], bool] | None" = None,
    ) -> None:
        self._context = context or {name}DomainContext()
        self._handle = handle
        self._refuses_when = refuses_when

    @property
    def context(self) -> {name}DomainContext:
        return self._context

    @property
    def topics(self) -> tuple[str, ...]:
        return self._context.topics

    def refusal_for(self, request: str) -> str:
        """The refusal, or empty when there is none.

        A caller that ignores this gets no protection, which is why the adapter
        itself calls it rather than leaving it to be remembered.
        """
        if self._refuses_when is not None and self._refuses_when(request):
            return f"I will not {{self._context.refuses}} - {{self._context.because}}"
        return ""

    def respond(self, request: str) -> tuple[str, bool]:
        """Returns (text, was_refused).

        The flag is separate from the text so a caller can tell a refusal from
        an answer without reading the words - a UI shows them differently, and a
        log counts them differently.
        """
        refusal = self.refusal_for(request)
        if refusal:
            return refusal, True
        if self._handle is None:
            return (
                "this device has nothing wired up for "
                + ", ".join(self._context.topics) + " yet", False)
        return self._handle(request), False


'''

WIRING_BODY = '''class {cls}:
    """Registers what this module provides.

    A REGISTRATION POINT, not a factory. It records what a host chose to make
    available and returns exactly that - so a component asking what it can use
    gets an answer that reflects a decision somebody made, rather than whatever
    happened to import.
    """

    def __init__(self) -> None:
        self._registered: dict[str, object] = {{}}

    def add(self, name: str, service: object) -> "{cls}":
        """Overwriting is deliberate and last-wins, so a host can replace a
        default without having to remove it first."""
        if not name.strip():
            raise ValueError("a registration needs a name")
        self._registered[name] = service
        return self

    def get(self, name: str) -> "object | None":
        """None when absent, never a stand-in. A caller that gets a silent
        placeholder cannot tell a working service from a missing one."""
        return self._registered.get(name)

    def names(self) -> tuple[str, ...]:
        return tuple(sorted(self._registered))

    def build(self) -> dict[str, object]:
        return dict(self._registered)
'''


def main() -> None:
    root = os.path.join("python", "src", "circle_ai", "domains")
    os.makedirs(root, exist_ok=True)
    init = os.path.join(root, "__init__.py")
    if not os.path.exists(init):
        io.open(init, "w", encoding="utf-8", newline="\n").write(
            '"""The vertical companion adapters and the wiring modules."""\n')

    written = 0
    for module, (suffix, purpose, topics, (refuses, because)) in sorted(DOMAINS.items()):
        name = "".join(part.capitalize() if part[:1].islower() else part
                       for part in module.replace(".", "").split())
        name = module.replace(".", "")
        body = HEADER.format(
            title=f"{module}: {purpose}."
        ) + ADAPTER.format(
            name=name, purpose=purpose,
            purpose_sentence=f"The companion's way into {purpose}.",
            topics=repr(tuple(topics)), refuses=refuses, because=because,
        )
        path = os.path.join(root, f"{suffix}.py")
        io.open(path, "w", encoding="utf-8", newline="\n").write(body)
        written += 1

    for module, (suffix, cls) in sorted(WIRING.items()):
        body = HEADER.format(
            title=f"{module}: what a host chose to make available."
        ) + WIRING_BODY.format(cls=cls)
        path = os.path.join(root, f"{suffix}.py")
        io.open(path, "w", encoding="utf-8", newline="\n").write(body)
        written += 1

    print(f"wrote {written} modules into {root}")


if __name__ == "__main__":
    main()
