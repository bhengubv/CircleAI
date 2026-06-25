// Circle33PhoneNumberProvisionerTests.cs
//
// (3.3.0) Tests for PhoneNumberProvisioner — buy + configure + persist
// orchestration over any ITelephonyCarrier.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Telephony;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CircleAI.Tests;

public class Circle33PhoneNumberProvisionerTests
{
    [Fact]
    public async Task Provision_BuysConfiguresAndStoresNumber()
    {
        var carrier = new FakeProvisioningCarrier();
        var store   = new InMemoryProvisionedNumberStore();
        var provisioner = new PhoneNumberProvisioner(carrier, store);

        var result = await provisioner.ProvisionAsync("US", new Uri("https://example.com/voice"));

        Assert.Equal("+15555550199", result.PhoneNumber);
        Assert.Equal("fake", result.CarrierId);

        Assert.Single(carrier.Provisioned);
        Assert.Single(carrier.WebhookConfigured);
        Assert.Equal("+15555550199", carrier.WebhookConfigured[0].Number);
        Assert.Equal(new Uri("https://example.com/voice"), carrier.WebhookConfigured[0].Webhook);

        var stored = await store.ListAsync();
        Assert.Single(stored);
        Assert.Equal("+15555550199", stored[0].PhoneNumber);
    }

    [Fact]
    public async Task Provision_PropagatesAreaCode()
    {
        var carrier = new FakeProvisioningCarrier();
        var provisioner = new PhoneNumberProvisioner(carrier);

        await provisioner.ProvisionAsync("US", new Uri("https://example.com/voice"), areaCode: "415");

        Assert.Equal("415", carrier.Provisioned[0].AreaCode);
    }

    [Fact]
    public async Task Provision_RejectsRelativeWebhook()
    {
        var provisioner = new PhoneNumberProvisioner(new FakeProvisioningCarrier());

        await Assert.ThrowsAsync<ArgumentException>(
            () => provisioner.ProvisionAsync("US", new Uri("/voice", UriKind.Relative)).AsTask());
    }

    [Fact]
    public async Task Provision_RejectsEmptyCountryCode()
    {
        var provisioner = new PhoneNumberProvisioner(new FakeProvisioningCarrier());

        await Assert.ThrowsAsync<ArgumentException>(
            () => provisioner.ProvisionAsync("", new Uri("https://example.com/voice")).AsTask());
    }

    [Fact]
    public async Task Provision_PropagatesWebhookConfigurationFailure()
    {
        var carrier = new FakeProvisioningCarrier { ThrowOnConfigureWebhook = true };
        var store = new InMemoryProvisionedNumberStore();
        var provisioner = new PhoneNumberProvisioner(carrier, store);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => provisioner.ProvisionAsync("US", new Uri("https://example.com/voice")).AsTask());

        // The number must not be persisted if webhook configuration failed.
        var stored = await store.ListAsync();
        Assert.Empty(stored);
    }

    [Fact]
    public async Task List_MergesStoreWithCarrierAuthoritative()
    {
        var carrier = new FakeProvisioningCarrier
        {
            ListResult =
            [
                new ProvisionedNumber("+15555550199", "fake", DateTimeOffset.UtcNow, 1m),
                new ProvisionedNumber("+15555550299", "fake", DateTimeOffset.UtcNow, 1m),
            ],
        };
        var store = new InMemoryProvisionedNumberStore();
        await store.SaveAsync(new ProvisionedNumber("+15555550199", "fake", DateTimeOffset.UtcNow, 1m));
        // store has 1, carrier has 2 — merge should be 2 distinct numbers.

        var provisioner = new PhoneNumberProvisioner(carrier, store);
        var merged = await provisioner.ListAsync();

        Assert.Equal(2, merged.Count);
        Assert.Contains(merged, n => n.PhoneNumber == "+15555550199");
        Assert.Contains(merged, n => n.PhoneNumber == "+15555550299");
    }

    [Fact]
    public async Task InMemoryStore_SaveListFindRemove_RoundtripsCleanly()
    {
        var store = new InMemoryProvisionedNumberStore();
        var n = new ProvisionedNumber("+15555550100", "test", DateTimeOffset.UtcNow, 1.50m);

        await store.SaveAsync(n);

        var list = await store.ListAsync();
        Assert.Single(list);

        var found = await store.FindAsync("+15555550100");
        Assert.NotNull(found);
        Assert.Equal(1.50m, found!.MonthlyRecurringCost);

        var notFound = await store.FindAsync("+15555550999");
        Assert.Null(notFound);

        await store.RemoveAsync("+15555550100");
        Assert.Empty(await store.ListAsync());
    }

    [Fact]
    public async Task InMemoryStore_Save_RejectsNull()
    {
        var store = new InMemoryProvisionedNumberStore();
        await Assert.ThrowsAsync<ArgumentNullException>(() => store.SaveAsync(null!).AsTask());
    }

    [Fact]
    public async Task InMemoryStore_SameNumberTwice_LastWriteWins()
    {
        var store = new InMemoryProvisionedNumberStore();
        await store.SaveAsync(new ProvisionedNumber("+15555550100", "v1", DateTimeOffset.UtcNow, 1m));
        await store.SaveAsync(new ProvisionedNumber("+15555550100", "v2", DateTimeOffset.UtcNow, 2m));

        var list = await store.ListAsync();
        Assert.Single(list);
        Assert.Equal("v2", list[0].CarrierId);
    }

    [Fact]
    public void DI_AddCircleAiTelephony_RegistersProvisionerAndStore()
    {
        var services = new ServiceCollection();
        services.AddCircleAiTelephony();
        using var sp = services.BuildServiceProvider();

        var provisioner = sp.GetRequiredService<PhoneNumberProvisioner>();
        Assert.NotNull(provisioner);

        var store = sp.GetRequiredService<IProvisionedNumberStore>();
        Assert.IsType<InMemoryProvisionedNumberStore>(store);
    }

    /// <summary>(3.3.0) Fake carrier that records every call and serves canned data.</summary>
    private sealed class FakeProvisioningCarrier : ITelephonyCarrier
    {
        public string CarrierId    => "fake";
        public bool   IsConfigured => true;

        public List<(string Country, string? AreaCode)>  Provisioned       { get; } = new();
        public List<(string Number, Uri Webhook)>        WebhookConfigured { get; } = new();
        public IReadOnlyList<ProvisionedNumber>          ListResult        { get; set; } = Array.Empty<ProvisionedNumber>();
        public bool                                       ThrowOnConfigureWebhook { get; set; }

        public ValueTask<ProvisionedNumber> ProvisionNumberAsync(
            string countryCode, string? areaCode = null, CancellationToken ct = default)
        {
            Provisioned.Add((countryCode, areaCode));
            return ValueTask.FromResult(new ProvisionedNumber(
                PhoneNumber:           "+15555550199",
                CarrierId:             CarrierId,
                ProvisionedAtUtc:      DateTimeOffset.UtcNow,
                MonthlyRecurringCost:  1.10m));
        }

        public ValueTask ConfigureInboundWebhookAsync(string phoneNumber, Uri inboundWebhook, CancellationToken ct = default)
        {
            if (ThrowOnConfigureWebhook)
            {
                throw new InvalidOperationException("Webhook configure failed for testing");
            }
            WebhookConfigured.Add((phoneNumber, inboundWebhook));
            return ValueTask.CompletedTask;
        }

        public ValueTask<ICallSession> DialAsync(
            string fromNumber, string toNumber, Uri streamUrl,
            OutboundDialOptions? options = null, CancellationToken ct = default)
            => throw new NotImplementedException();

        public ValueTask<IReadOnlyList<ProvisionedNumber>> ListNumbersAsync(CancellationToken ct = default)
            => ValueTask.FromResult(ListResult);
    }
}
