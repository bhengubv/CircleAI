// CapabilityManifestSkillStoreTests.cs
//
// The assistant describing itself is only an improvement if it describes itself
// HONESTLY. A capability catalogue that let it claim planned features would be
// a machine for confident lying — the exact failure the whole capabilities.json
// effort exists to end.
//
// So these tests care less about "can it list things" and more about "does a
// planned or rejected capability come with instructions that forbid claiming
// it". That is the load-bearing property.

using System.Linq;
using System.Threading.Tasks;
using CircleAI.Skills;
using Xunit;

namespace CircleAI.Tests;

public sealed class CapabilityManifestSkillStoreTests
{
    // Deliberately mirrors the real manifest's shape, including one entry per
    // status, so the honesty rules are exercised without depending on the
    // current contents of capabilities.json.
    private const string Manifest = """
    {
      "Capabilities": [
        {
          "Id": "tools.calling",
          "Name": "Tool calling",
          "Status": "shipping",
          "Summary": "Describes tools to the model and executes what it calls.",
          "Package": "CircleAI.Hosting",
          "Limits": [ "Verified with two trivial tools." ],
          "Measured": {
            "Device": "Huawei MAR-LX1M",
            "Date": "2026-07-20",
            "Result": "Both probes fired for real."
          }
        },
        {
          "Id": "voice.ondevice",
          "Name": "On-device speech",
          "Status": "scaffold",
          "Summary": "Local speech to text and back.",
          "Package": "CircleAI.Voice",
          "Requires": [ "Speech model weights. None ship." ],
          "Limits": [ "No speech model is catalogued." ]
        },
        {
          "Id": "speech.catalogue",
          "Name": "Speech model ladder",
          "Status": "planned",
          "Summary": "Catalogue open speech models so BestFit selects them.",
          "Package": "CircleAI.Core",
          "Limits": [ "Not built." ]
        },
        {
          "Id": "voice.platform",
          "Name": "Platform speech wrapper",
          "Status": "rejected",
          "Summary": "Would use the phone's own recogniser.",
          "Package": "(none)",
          "Limits": [ "Rejected on the de-Googled rule." ]
        }
      ]
    }
    """;

    private static CapabilityManifestSkillStore Store() => new(Manifest);

    [Fact]
    public async Task Lists_EveryCapability()
    {
        var all = await Store().ListAsync();
        Assert.Equal(4, all.Count);
    }

    [Fact]
    public async Task Description_LeadsWithStatus_SoItCannotBeReadAsAPlainCapability()
    {
        var all = await Store().ListAsync();

        var planned = all.Single(s => s.Id == "speech.catalogue");
        Assert.StartsWith("[planned]", planned.Description);

        var shipping = all.Single(s => s.Id == "tools.calling");
        Assert.StartsWith("[shipping]", shipping.Description);
    }

    [Theory]
    [InlineData("speech.catalogue")]  // planned
    [InlineData("voice.platform")]    // rejected
    [InlineData("voice.ondevice")]    // scaffold
    public async Task NonShippingCapabilities_TellTheModelNotToClaimThem(string id)
    {
        var detail = await Store().GetAsync(id);

        Assert.NotNull(detail);
        Assert.Contains("Do NOT claim", detail!.Instructions, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ShippingCapability_MayBeStatedPlainly()
    {
        var detail = await Store().GetAsync("tools.calling");

        Assert.NotNull(detail);
        Assert.Contains("works", detail!.Instructions, System.StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Do NOT claim", detail.Instructions, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MeasuredEvidence_ReachesTheModel()
    {
        // Device evidence is the strongest thing the assistant can say about
        // itself — it must not be dropped on the way to the prompt.
        var detail = await Store().GetAsync("tools.calling");

        Assert.Contains("Huawei MAR-LX1M", detail!.Instructions);
        Assert.Contains("2026-07-20", detail.Instructions);
    }

    [Fact]
    public async Task LimitsAndRequires_ReachTheModel()
    {
        var detail = await Store().GetAsync("voice.ondevice");

        Assert.Contains("None ship", detail!.Instructions);
        Assert.Contains("No speech model is catalogued", detail.Instructions);
    }

    [Fact]
    public async Task Search_MatchesADottedIdSegment()
    {
        // "can you do voice?" must reach voice.* entries. Without splitting the
        // dotted Id into tags, it matches nothing at all.
        var hits = await Store().SearchAsync("voice");

        Assert.Contains(hits, h => h.Id == "voice.ondevice");
        Assert.Contains(hits, h => h.Id == "voice.platform");
    }

    [Fact]
    public async Task Search_EmptyQuery_ReturnsNothing()
        => Assert.Empty(await Store().SearchAsync("   "));

    [Fact]
    public async Task Store_IsReadOnly()
    {
        var store = Store();

        await Assert.ThrowsAsync<System.NotSupportedException>(() =>
            store.UpsertAsync("x", new SkillDraft("n", "d", "i", System.Array.Empty<string>())));

        await Assert.ThrowsAsync<System.NotSupportedException>(() => store.DeleteAsync("tools.calling"));
    }

    [Fact]
    public async Task MalformedManifest_DegradesToEmpty_RatherThanBreakingChat()
    {
        // Losing self-knowledge must never stop the assistant answering
        // ordinary questions.
        var store = new CapabilityManifestSkillStore("{ this is not json");
        Assert.Empty(await store.ListAsync());
    }

    [Fact]
    public async Task TheRealEmbeddedManifest_Loads()
    {
        // Guards the csproj wiring: LogicalName must match ResourceName, or the
        // store silently serves an empty catalogue on device.
        var all = await CapabilityManifestSkillStore.Default.ListAsync();

        Assert.NotEmpty(all);
        Assert.Contains(all, s => s.Id == "tools.calling");
    }
}
