// SendAppTests.cs
//
// Handing Circle AI to the next phone.
//
// THE ONLY ROUTE THAT NEEDS NOTHING. A phone with nothing on it cannot reach a
// store, and cannot reach the mesh either - that would need AetherNetService, which
// is another install, which is the same problem one step along. Somebody sending
// the installer over Bluetooth needs no shop, no account and no signal.
//
// It is a FEATURE, which is why it is a page in Services rather than a block on
// Home or a row in Settings: Settings changes something you have, Services is the
// things you can do, and this is a thing you do.

using Bunit;
using CircleAI.Assistant;
using CircleAI.Samples.Shared.Pages;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CircleAI.Samples.Ui.Tests;

public sealed class SendAppTests : TestContext
{
    private sealed class Pairing(params OfferableApp[] offerable) : IAppPairing
    {
        public PairingFacts Facts => new(true, true, offerable.Length > 0);
        public IReadOnlyList<OfferableApp> Offerable { get; } = offerable;
        public int Offers { get; private set; }

        public Task<bool> OfferAsync(CancellationToken ct = default)
        {
            Offers++;
            return Task.FromResult(true);
        }

        public bool OpenInstallPermission() => true;
    }

    private IRenderedComponent<SendApp> Render(IAppPairing pairing)
    {
        Services.AddSingleton(pairing);
        return RenderComponent<SendApp>();
    }

    [Fact]
    public void It_lists_both_halves_with_their_sizes()
    {
        // BOTH, OR THE RECEIVER IS STUCK WHERE THE SENDER WAS: screens alone cannot
        // answer anything, and the brain alone has no icon.
        //
        // SIZES BEFORE SENDING, because the brain is tens of megabytes and whoever
        // receives it may be paying for every one. Not pinned to a decimal
        // separator - the page formats in the current culture, as it should.
        var page = Render(new Pairing(
            new OfferableApp("Circle AI brain", 32_800_000),
            new OfferableApp("Circle AI", 12_500_000)));

        Assert.Contains("Circle AI brain", page.Markup);
        Assert.Matches(@"31[.,]3 MB", page.Markup);
        Assert.Matches(@"11[.,]9 MB", page.Markup);
    }

    [Fact]
    public void It_says_no_shop_no_account_no_data()
    {
        var page = Render(new Pairing(new OfferableApp("Circle AI", 12_500_000)));

        Assert.Contains("Bluetooth", page.Markup);
        Assert.Contains("No shop, no account, no data", page.Markup);
    }

    [Fact]
    public void Sending_opens_the_share_sheet()
    {
        var pairing = new Pairing(new OfferableApp("Circle AI", 12_500_000));
        var page = Render(pairing);

        page.Find("button.pairing-go").Click();

        Assert.Equal(1, pairing.Offers);
    }

    [Fact]
    public void A_phone_with_nothing_to_give_offers_no_button()
    {
        // Rather than a button that opens an empty share sheet, which looks like a
        // broken feature instead of a phone that has nothing yet.
        var page = Render(new Pairing());

        Assert.Contains("nothing to send", page.Markup);
        Assert.Empty(page.FindAll("button.pairing-go"));
    }
}
