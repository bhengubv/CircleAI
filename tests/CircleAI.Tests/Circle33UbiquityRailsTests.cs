// Circle33UbiquityRailsTests.cs
//
// (3.3.0) Smoke tests for UBI rail defaults.

using System.Linq;
using CircleAI.Distribution.Ubiquity;
using Xunit;

namespace CircleAI.Tests;

public class Circle33UbiquityRailsTests
{
    [Fact]
    public void OemPreloadCatalog_ListsExpectedPartners()
    {
        var c = new DefaultOemPreloadCatalog();
        Assert.Contains("Tecno", c.Partners);
        Assert.Contains("Xiaomi", c.Partners);
    }

    [Fact]
    public void CarrierPreloadCatalog_ListsExpectedCarriers()
    {
        var c = new DefaultCarrierPreloadCatalog();
        Assert.Contains("MTN",     c.Carriers);
        Assert.Contains("Vodacom", c.Carriers);
    }

    [Fact]
    public void PwaFallback_HasUrl()
    {
        Assert.True(new DefaultPwaFallback().PwaUrl.IsAbsoluteUri);
    }

    [Fact]
    public void SideloadFormats_CoversBigThree()
    {
        var f = new DefaultSideloadChannel().Formats;
        Assert.Contains("APK", f);
        Assert.Contains("IPA", f);
        Assert.Contains("MSIX", f);
    }

    [Fact]
    public void LinuxRepos_SixFlavours()
    {
        Assert.Equal(6, new DefaultLinuxRepoFanout().Repos.Count);
    }

    [Fact]
    public void AiPersonalityWizard_FourPresets()
    {
        var w = new DefaultAiPersonalityWizard();
        Assert.Equal(4, w.Presets.Count);
    }

    [Fact]
    public void ComplianceCertifications_Three()
    {
        Assert.Equal(3, new DefaultComplianceCertifications().Certifications.Count);
    }

    [Fact]
    public void PrivacyRegulationCompliance_FourLaws()
    {
        Assert.Equal(4, new DefaultPrivacyRegulationCompliance().Laws.Count);
    }

    [Fact]
    public void PricingMatrix_FiveTiers()
    {
        var m = new DefaultPricingMatrix();
        Assert.Equal(5, m.All.Count);
        Assert.Contains(m.All, t => t.Name == "free");
        Assert.Contains(m.All, t => t.Name == "paid" && t.MonthlyPriceLocal == 19m);
    }

    [Fact]
    public void RevenueShare_AuthorIs70Percent()
    {
        Assert.Equal(0.70, new DefaultPluginMarketplaceRevenueShare().AuthorShare);
    }

    [Fact]
    public void CurrencyFormatter_PrintsCode()
    {
        var f = new DefaultCurrencyFormatter();
        Assert.Contains("ZAR", f.Format(19m, "ZAR"));
    }

    [Fact]
    public void CulturalGreetings_KnownForZulu()
    {
        Assert.Equal("Sawubona", new DefaultCulturalGreetings().GreetingFor("zul"));
    }

    [Fact]
    public void CulturalNameRecogniser_RecognisesYoruba()
    {
        Assert.True(new DefaultCulturalNameRecogniser().RecognisesLanguage("yor"));
    }

    [Fact]
    public void CrossBorderCorridors_Three()
    {
        Assert.Equal(3, new DefaultCrossBorderCorridors().Corridors.Count);
    }

    [Fact]
    public void LowRamPhone_SupportsFiveTwelve()
    {
        Assert.True(new DefaultLowRamPhoneSupport().SupportsRamMb(512));
    }

    [Fact]
    public void LowCpu_SupportsSixHundred()
    {
        Assert.True(new DefaultLowCpuOptimization().SupportsClockMhz(600));
    }

    [Fact]
    public void EmailConnectors_SevenProviders()
    {
        Assert.Equal(7, new DefaultEmailConnectorRegistry().Providers.Count);
    }

    [Fact]
    public void CalendarConnectors_FiveProviders()
    {
        Assert.Equal(5, new DefaultCalendarConnectorRegistry().Providers.Count);
    }

    [Fact]
    public void LawfulInterceptPosture_HoldsTheRulingLine()
    {
        var s = new DefaultLawfulInterceptCompliance().Posture;
        Assert.Contains("comms permanently blind", s);
    }

    [Fact]
    public void SustainablePerUserMath_HoldsR19ToR3_8()
    {
        var c = new DefaultSustainablePerUserCostMath();
        Assert.Equal(19m, c.MonthlyRevenuePerUser);
        Assert.True(c.MonthlyMarginalCostPerUser < c.MonthlyRevenuePerUser);
    }

    [Fact]
    public void FamilyAiSharing_SupportsUpTo6()
    {
        Assert.Equal(6, new DefaultFamilyAiSharing().MaxMembers);
    }

    [Fact]
    public void ReferralProgramme_R19MonthFree()
    {
        var r = new DefaultReferralProgramme();
        Assert.Equal(19m, r.RewardLocal);
        Assert.Equal("ZAR", r.Currency);
    }

    [Fact]
    public void GroupNetworkEffects_Three()
    {
        Assert.Equal(3, new DefaultGroupNetworkEffects().GroupTypes.Count);
    }

    [Fact]
    public void ChildProtectionMode_DoubleCompliant()
    {
        var m = new DefaultChildProtectionMode();
        Assert.True(m.CoppaCompliant);
        Assert.True(m.GdprKCompliant);
    }

    [Fact]
    public void IndigenousDataSovereignty_FollowsCare()
    {
        Assert.Equal("CARE Principles", new DefaultIndigenousDataSovereignty().Standard);
    }

    [Fact]
    public void ReligiousAccommodation_HasModes()
    {
        var m = new DefaultReligiousAccommodation();
        Assert.Contains("prayer times", m.SupportedModes);
        Assert.Contains("Shabbat mode",  m.SupportedModes);
    }
}
