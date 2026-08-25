"""Generate the services list from the modules that actually exist.

WHY THIS IS GENERATED. The first version of Capabilities.cs was thirty-nine tiles
I wrote by hand. That is an opinion about what the app does, not a description -
it overlapped the real module list only by accident, and a new module added to
src/ would never appear in the app because nothing connected the two.

So the list is derived. Every project under src/ is classified exactly once:

  SERVICE         something a person asks for. It gets a tile.
  INFRASTRUCTURE  something the machine needs. It gets none.

Anything unclassified is an ERROR rather than a silent omission - that is the
whole point. A module somebody adds next month either appears in the app or fails
this script, and either is better than the app quietly not knowing about it.

    python tools/gen-services/gen_services.py
    python tools/gen-services/gen_services.py --check    (used by the tests)
"""
import pathlib
import re
import sys

REPO = pathlib.Path(__file__).resolve().parent.parent.parent
SRC = REPO / "src"
OUT = REPO / "samples" / "CircleAI.Samples.It.Contracts" / "Capabilities.cs"

# -- what the machine needs, not what a person asks for -----------------------
#
# Kept as prefixes because the families are large and consistent: every
# Networking.* is plumbing, every Security.* is plumbing. Listing them one by one
# would be a list nobody maintains.
INFRASTRUCTURE = (
    "Accessibility", "Aether", "AetherNet", "Agents", "Ambient", "BuildFarm",
    "Cast", "CodeUnderstanding", "ContentPolicy", "Core", "DepBot", "Desktop",
    "Device", "DevTools", "Distribution", "DocAnalytics", "Domain", "Embeddings",
    "Federation", "Hosting", "Identity", "Inference", "Inputs", "Integration",
    "IoT", "Languages.Language", "Maui", "Mesh", "MicroAgents", "ModelAlignment",
    "Networking", "Observability", "Observer", "Operator", "Orchestration",
    "Personality", "Pipelines", "Plugins", "Realtime", "Runtime", "SDD",
    "SelfBench", "Security", "Simulation", "Skills", "Spatial", "Speech", "Sync",
    "Telephony", "Testing", "Tools", "Voice", "Wearable", "Web",
    "WindowsAutomation", "Workflows", "Vision.Cloud", "Companion", "Knowledge",
    "Memory", "Collaboration", "MediaHub", "Commerce.Integration",
    "Inference.Server", "Safety.Child", "Personal",
)

# -- module -> (group, title, icon, opener, optional route) -------------------
#
# The WORDS are human and have to be: "Agriculture" is a namespace, "Farming" is
# what somebody calls it. The openers are first sentences rather than categories,
# because a tile exists to get somebody past the blank page.
SERVICES = {
    # Money - what a person earns and owes
    "Career":            ("Money", "Your CV", "\U0001F4C4", "Help me build my CV", "career"),
    "HR":                ("Money", "Work rights", "\U0001F4CB", "I have a question about my job or my rights at work"),
    "Banking":           ("Money", "Banking", "\U0001F3E6", "Help me understand my bank account"),
    "Personal.Finance":  ("Money", "Money", "\U0001F4B0", "Help me plan my money this month"),
    "Commerce":          ("Work and business", "Buying and selling", "\U0001F6D2", "Help me sell something"),
    "Commerce.Accounting": ("Work and business", "Bookkeeping", "\U0001F9EE", "Help me keep my books"),
    "Commerce.Finance":  ("Work and business", "Invoices", "\U0001F9FE", "Help me write an invoice"),
    "Business":          ("Work and business", "My business", "\U0001F4BC", "Help me run my small business"),
    "BusinessOps":       ("Work and business", "Running it", "⚙", "Help me organise my business"),
    "AutonomousBiz":     ("Work and business", "Growing it", "\U0001F680", "Help me grow my business"),
    "CRM":               ("Work and business", "Customers", "\U0001F91D", "Help me keep track of my customers"),
    "Markets":           ("Money", "Markets", "\U0001F4C8", "Explain what is happening in the markets"),
    "Retail":            ("Trades and land", "Shop", "\U0001F3EA", "Help me run my shop"),
    "Logistics":         ("Trades and land", "Deliveries", "\U0001F69A", "Help me plan deliveries"),
    "Construction":      ("Trades and land", "Building", "\U0001F3D7", "Help me with a building job"),
    "Agriculture":       ("Trades and land", "Farming", "\U0001F331", "Help me with my crops or livestock"),
    "Hospitality":       ("Trades and land", "Hosting", "\U0001F374", "Help me run a place that feeds people"),
    "Beauty":            ("Trades and land", "Hair and beauty", "\U0001F485", "Help me with my salon"),

    # Health and safety
    "Healthcare":        ("Health", "Health", "❤", "I have a health question"),
    "Personal.Health":   ("Health", "My health", "\U0001FA7A", "Help me keep track of my health"),
    "Personal.Mental":   ("Health", "How I feel", "\U0001F9E0", "I want to talk about how I am feeling"),
    "Fitness":           ("Health", "Fitness", "\U0001F3C3", "Help me get fitter"),
    "Food":              ("Health", "Food", "\U0001F35B", "Help me plan meals"),
    "Safety":            ("Health", "Staying safe", "\U0001F6E1", "Help me stay safe"),
    "Elderly":           ("Health", "Older family", "\U0001F9D3", "Help me care for an older relative"),
    "Pets":              ("Health", "Pets", "\U0001F415", "I have a question about my pet"),

    # Family and home
    "Family":            ("Family and home", "Family", "\U0001F46A", "Help me with something at home"),
    "Kids":              ("Family and home", "Children", "\U0001F9D2", "Help me with my children"),
    "Parenting":         ("Family and home", "Parenting", "\U0001F37C", "I have a parenting question"),
    "Relationships":     ("Family and home", "Relationships", "\U0001F49E", "I want to talk about a relationship"),
    "Home":              ("Family and home", "Home", "\U0001F3E0", "Help me sort something out at home"),
    "Energy":            ("Family and home", "Electricity", "⚡", "Help me with electricity and load shedding"),
    "RealEstate":        ("Family and home", "Housing", "\U0001F511", "I have a question about renting or housing"),

    # Learning
    "Education":         ("Learning", "Study help", "\U0001F4DA", "Help me study"),
    "Languages":         ("Learning", "Languages", "\U0001F310", "Help me with a language", "languages"),
    "Languages.Translation": ("Learning", "Interpret", "\U0001F524", "Interpret between two languages", "interpret"),
    "Research":          ("Learning", "Research", "\U0001F50E", "Help me research something"),
    "CodeAgent":         ("Learning", "Code", "\U0001F4BB", "Help me with some code"),

    # Making things
    "Documents":         ("Making things", "Write something", "✍", "Help me write something"),
    "Presentations":     ("Making things", "A presentation", "\U0001F4CA", "Help me make a presentation"),
    "Charts":            ("Making things", "A chart", "\U0001F4C9", "Help me make a chart of some numbers"),
    "Visualization":     ("Making things", "Show me", "\U0001F5BC", "Draw me a picture of this"),
    "Vision":            ("Making things", "Read a picture", "\U0001F4F7", "Look at a picture and tell me what it says", "chat"),
    "Search":            ("Making things", "Look it up", "\U0001F50D", "Look something up for me"),
    "Creative":          ("Making things", "Make something up", "✨", "Help me write something creative"),

    # Getting by
    "Legal":             ("Getting by", "Legal", "⚖", "I have a legal question"),
    "Civic":             ("Getting by", "Government", "\U0001F3DB", "Help me deal with a government office"),
    "Community":         ("Getting by", "Community", "\U0001F91D", "Help me with something in my community"),
    "Faith":             ("Getting by", "Faith", "\U0001F54A", "I want to talk about my faith"),
    "Social":            ("Getting by", "People", "\U0001F4AC", "Help me write a message to somebody"),
    "Travel":            ("Getting by", "Travel", "\U0001F68C", "Help me plan a trip"),
    "Tourism":           ("Getting by", "Places to go", "\U0001F5FA", "Tell me about a place"),

    # For enjoyment
    "Music":             ("For enjoyment", "Music", "\U0001F3B5", "Talk to me about music"),
    "Sports":            ("For enjoyment", "Sport", "⚽", "Talk to me about sport"),
    "Media":             ("For enjoyment", "Watch and read", "\U0001F4FA", "Recommend me something to watch or read"),
    "Video":             ("For enjoyment", "Video", "\U0001F3AC", "Help me with a video"),
    "Games":             ("For enjoyment", "Games", "\U0001F3B2", "Play a game with me"),
    "Gaming":            ("For enjoyment", "Gaming", "\U0001F579", "Talk to me about gaming"),
}

# NO GROUP OVER EIGHT TILES. Four across and two rows is what fits a 344px screen
# under the chips, and a ninth tile is the one nobody scrolls to find - which is
# the same as not having it. "Money and work" was eighteen and scrolled.
GROUP_ORDER = [
    "Money", "Work and business", "Trades and land", "Health", "Family and home",
    "Learning", "Making things", "Getting by", "For enjoyment",
]

MAX_PER_GROUP = 8


def modules():
    return sorted(p.name.removeprefix("CircleAI.") for p in SRC.glob("CircleAI.*") if p.is_dir())


def is_infrastructure(name):
    return any(name == i or name.startswith(i + ".") for i in INFRASTRUCTURE)


def classify():
    """Every module, sorted into services and plumbing. Unclassified is an error."""
    unknown = [m for m in modules() if not is_infrastructure(m) and m not in SERVICES]
    return unknown


def render():
    lines = [
        "// Capabilities.cs",
        "//",
        "// GENERATED by tools/gen-services/gen_services.py. Do not edit by hand.",
        "//",
        "// WHAT THE APP CAN DO, DERIVED FROM WHAT IS ACTUALLY BUILT. The repository",
        "// carries 165 projects and the app used to surface one of them - somebody with",
        "// the whole source tree open could not have said it does agriculture, so a",
        "// person holding the phone had no chance.",
        "//",
        "// The first version of this file was thirty-nine tiles written by hand: an",
        "// opinion about what the app does rather than a description of it, overlapping",
        "// the real module list only by accident. Now every project under src/ is",
        "// classified exactly once - a service a person asks for, or plumbing the",
        "// machine needs - and anything unclassified FAILS the generator instead of",
        "// quietly not appearing.",
        "//",
        "// Every tile opens the conversation pointed at the thing. That is what makes",
        "// breadth free: sixty tiles need sixty first sentences, not sixty screens, and",
        "// it is how an assistant works - you ask it, you do not navigate to it.",
        "",
        "namespace CircleAI.Samples.It;",
        "",
        "/// <summary>One thing the app can help with.</summary>",
        "/// <param name=\"Title\">What it is, in the words somebody would use.</param>",
        "/// <param name=\"Icon\">A glyph, for scanning rather than reading.</param>",
        "/// <param name=\"Opener\">",
        "/// What to ask when it is tapped. A FIRST SENTENCE, not a category: a tile",
        "/// exists to get somebody past the blank page, and \"Help me plan my money\" is",
        "/// a question the assistant can answer where \"Money\" is not.",
        "/// </param>",
        "/// <param name=\"Route\">",
        "/// A screen, where one genuinely exists. Only a handful do - the rest are",
        "/// answered by asking, and forty screens for forty capabilities is how an",
        "/// assistant becomes a menu system.",
        "/// </param>",
        "public sealed record Capability(string Title, string Icon, string Opener, string? Route = null);",
        "",
        "/// <summary>A section of the grid.</summary>",
        "public sealed record CapabilityGroup(string Title, Capability[] Items);",
        "",
        "/// <summary>Everything the app can help with, grouped for a grid.</summary>",
        "public static class Capabilities",
        "{",
        "    /// <summary>The groups, in the order they are shown.</summary>",
        "    /// <remarks>",
        "    /// ORDERED BY WHAT MATTERS ON A CHEAP PHONE HERE, not by what is most",
        "    /// impressive: money and work first, because that is what somebody needs an",
        "    /// assistant for when they cannot afford one. Enjoyment is last and is still",
        "    /// there, because an assistant that only does chores is one people stop",
        "    /// opening.",
        "    /// </remarks>",
        "    public static IReadOnlyList<CapabilityGroup> All { get; } =",
        "    [",
    ]

    for group in GROUP_ORDER:
        items = [(m, v) for m, v in SERVICES.items() if v[0] == group]
        lines.append(f'        new("{group}",')
        lines.append("        [")
        for module, v in items:
            _, title, icon, opener, *rest = v
            route = f', "{rest[0]}"' if rest else ""
            lines.append(f'            // src/CircleAI.{module}')
            lines.append(f'            new("{title}", "{icon}", "{opener}"{route}),')
        lines.append("        ]),")
        lines.append("")

    lines += ["    ];", "}", ""]
    return "\n".join(lines)


def main():
    # A group that does not fit is a group somebody scrolls, and a tile below the
    # fold is a capability that does not exist.
    import collections
    counts = collections.Counter(v[0] for v in SERVICES.values())
    over = {g: n for g, n in counts.items() if n > MAX_PER_GROUP}
    if over:
        print(f"groups over {MAX_PER_GROUP} tiles, so they would scroll:", file=sys.stderr)
        for g, n in over.items():
            print(f"  {g}: {n}", file=sys.stderr)
        return 1

    missing = [g for g in counts if g not in GROUP_ORDER]
    if missing:
        print(f"groups not in GROUP_ORDER: {missing}", file=sys.stderr)
        return 1

    unknown = classify()
    if unknown:
        print("UNCLASSIFIED MODULES - add to SERVICES or INFRASTRUCTURE:", file=sys.stderr)
        for m in unknown:
            print(f"  {m}", file=sys.stderr)
        return 1

    text = render()
    if "--check" in sys.argv:
        current = OUT.read_text(encoding="utf-8") if OUT.exists() else None
        if current != text:
            print("Capabilities.cs is stale - re-run this script", file=sys.stderr)
            return 1
        print("Capabilities.cs is current")
        return 0

    OUT.write_text(text, encoding="utf-8")
    total = len(SERVICES)
    print(f"{total} services in {len(GROUP_ORDER)} groups, from {len(modules())} modules")
    print(f"  -> {OUT.relative_to(REPO)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
