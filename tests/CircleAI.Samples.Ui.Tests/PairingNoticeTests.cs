// PairingNoticeTests.cs
//
// The screen that tells somebody half of this app is missing.
//
// It has two jobs that pull in opposite directions: say so unmissably when the
// brain is not there, and render NOTHING when everything is fine - because it sits
// at the top of Home, where furniture would be in the way every single day.

using Bunit;
using CircleAI.Assistant;
using CircleAI.Samples.Shared.Components;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CircleAI.Samples.Ui.Tests;

public sealed class PairingNoticeTests : TestContext
{
    private sealed class FakePairing(PairingFacts facts, params OfferableApp[] offerable) : IAppPairing
    {
        public PairingFacts Facts { get; } = facts;
        public IReadOnlyList<OfferableApp> Offerable { get; } = offerable;
        public int Offers { get; private set; }
        public int PermissionOpens { get; private set; }

        public Task<bool> OfferAsync(CancellationToken ct = default)
        {
            Offers++;
            return Task.FromResult(true);
        }

        public bool OpenInstallPermission()
        {
            PermissionOpens++;
            return true;
        }
    }

    private IRenderedComponent<PairingNotice> Render(IAppPairing pairing)
    {
        Services.AddSingleton(pairing);
        return RenderComponent<PairingNotice>();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_complete_phone_renders_nothing_at_all(bool canOffer)
    {
        // IT LIVES AT THE TOP OF HOME. Anything at all here is something every
        // person sees every day for no reason - including on a phone that COULD
        // pass Circle AI on, because offering to do that is a feature and features
        // live in Services, not stapled to the home screen.
        var page = Render(new FakePairing(new PairingFacts(true, true, canOffer)));

        Assert.Equal(string.Empty, page.Markup.Trim());
    }

    [Fact]
    public void A_missing_brain_is_named_and_explained()
    {
        var page = Render(new FakePairing(new PairingFacts(false, true, false)));

        // NAMED THE WAY THE PRODUCT NAMES IT, from one owner - so this and the
        // link's own refusal are plainly about the same thing.
        Assert.Contains(AssistantPairingCopy.Name, page.Markup);
        Assert.Contains("two parts", page.Markup);
    }

    [Fact]
    public void It_says_how_to_get_it_without_a_shop_or_a_signal()
    {
        // THE WHOLE POINT. A phone with nothing on it cannot reach a store or the
        // mesh; somebody handing over the file needs neither.
        var page = Render(new FakePairing(new PairingFacts(false, true, false)));

        Assert.Contains("Bluetooth", page.Markup);
        Assert.Contains("No shop, no account, no data", page.Markup);
    }

    [Fact]
    public void A_phone_that_may_not_install_is_offered_the_switch()
    {
        // FROM ANDROID 8 IT IS A SETTINGS SCREEN, NOT A DIALOG, so the only honest
        // thing is to say so and take them to it.
        var pairing = new FakePairing(new PairingFacts(false, MayInstall: false, CanOffer: false));
        var page = Render(pairing);

        page.Find("button.pairing-go").Click();

        Assert.Equal(1, pairing.PermissionOpens);
    }

    [Fact]
    public void A_phone_that_may_install_is_not_nagged_about_permission()
    {
        var page = Render(new FakePairing(new PairingFacts(false, MayInstall: true, CanOffer: false)));

        Assert.DoesNotContain("Open that setting", page.Markup);
    }

}
