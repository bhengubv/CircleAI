// TransportSelectorTests.cs
//
// OPEN-GAPS A10: eleven transports stood behind ITransportSelector, which nothing implemented,
// and everything above rode exactly one transport. These pin the cascade the contract states,
// as connectivity and policy narrow it, and the transport that sends by it.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using CircleAI.Networking;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CircleAI.Tests;

public sealed class TransportSelectorTests
{
    private static readonly NetworkPayload Plain = NetworkPayload.Create(new byte[] { 1, 2, 3 });

    private static NetworkContext Up(ConnectivityState state, params TransportKind[] up) =>
        new(state, up.FirstOrDefault(), up, null, null, null, 0, DateTimeOffset.UnixEpoch);

    private static readonly TransportKind[] Everything = Enum.GetValues<TransportKind>();

    // ── The cascade ─────────────────────────────────────────────────────

    [Fact]
    public void Online_with_everything_up_is_the_order_the_contract_states_and_never_udp()
    {
        var cascade = new CascadeTransportSelector().GetCascade(Plain, Up(ConnectivityState.Online, Everything));
        Assert.Equal(CascadeTransportSelector.DefaultCascade, cascade);
        Assert.DoesNotContain(TransportKind.Udp, cascade);
    }

    [Fact]
    public void Only_what_is_up_is_offered_with_the_store_and_forward_floor_last()
    {
        var cascade = new CascadeTransportSelector().GetCascade(Plain,
            Up(ConnectivityState.Online, TransportKind.Bluetooth, TransportKind.Http));
        Assert.Equal([TransportKind.Http, TransportKind.Bluetooth, TransportKind.Dtn, TransportKind.LocalStore], cascade);
    }

    [Fact]
    public void Connectivity_narrows_it()
    {
        var selector = new CascadeTransportSelector();
        Assert.Equal([TransportKind.Dtn, TransportKind.LocalStore],
            selector.GetCascade(Plain, Up(ConnectivityState.Offline, Everything)));
        Assert.Equal([TransportKind.WiFi, TransportKind.Bluetooth, TransportKind.NearLink, TransportKind.Aether, TransportKind.Dtn, TransportKind.LocalStore],
            selector.GetCascade(Plain, Up(ConnectivityState.MeshOnly, Everything)));
        Assert.DoesNotContain(TransportKind.Http, selector.GetCascade(Plain, Up(ConnectivityState.LocalOnly, Everything)));
        Assert.Contains(TransportKind.Tcp, selector.GetCascade(Plain, Up(ConnectivityState.LocalOnly, Everything)));
    }

    [Fact]
    public void An_emergency_goes_mesh_first()
    {
        var sos = NetworkPayload.Create(new byte[] { 9 }, priority: MessagePriority.Emergency);
        var cascade = new CascadeTransportSelector().GetCascade(sos,
            Up(ConnectivityState.Online, TransportKind.Http, TransportKind.Aether));
        Assert.Equal(TransportKind.Aether, cascade[0]);
        Assert.Equal(TransportKind.Http, cascade[1]);
    }

    [Fact]
    public void Policy_is_honoured()
    {
        var all = Up(ConnectivityState.Online, Everything);

        Assert.Equal([TransportKind.Bluetooth],
            new CascadeTransportSelector(new Rules { Force = TransportKind.Bluetooth }).GetCascade(Plain, all));
        Assert.Equal(TransportKind.WiFi,
            new CascadeTransportSelector(new Rules { Mesh = true }).SelectBest(Plain, all));
        Assert.DoesNotContain(TransportKind.Grpc,
            new CascadeTransportSelector(new Rules { Cloud = false }).GetCascade(Plain, all));
        Assert.DoesNotContain(TransportKind.LocalStore,
            new CascadeTransportSelector(new Rules { Queue = false }).GetCascade(Plain, all));
        Assert.DoesNotContain(TransportKind.Tcp,
            new CascadeTransportSelector(new Rules { Refuse = TransportKind.Tcp }).GetCascade(Plain, all));
    }

    // ── Sending by it ───────────────────────────────────────────────────

    [Fact]
    public async Task A_payload_goes_the_first_way_that_is_up_and_falls_through_a_failure()
    {
        var http = new Fake(TransportKind.Http) { Fails = true };
        var tcp  = new Fake(TransportKind.Tcp) { Up = false };
        var bt   = new Fake(TransportKind.Bluetooth);
        await using var transport = new SelectingTransport([bt, tcp, http], new CascadeTransportSelector());

        await transport.SendAsync(Plain);

        Assert.Single(http.Tried);           // tried, failed
        Assert.Empty(tcp.Tried);             // down, never tried
        Assert.Single(bt.Sent);              // took it
        Assert.Equal(TransportKind.Bluetooth, transport.LastUsed);
    }

    [Fact]
    public async Task When_every_way_fails_it_says_each_reason()
    {
        await using var transport = new SelectingTransport(
            [new Fake(TransportKind.Http) { Fails = true }, new Fake(TransportKind.WiFi) { Fails = true }],
            new CascadeTransportSelector());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => transport.SendAsync(Plain));
        Assert.Contains("Http", error.Message, StringComparison.Ordinal);
        Assert.Contains("WiFi", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task What_any_of_them_receives_comes_out_of_one_stream()
    {
        var a = new Fake(TransportKind.Http);
        var b = new Fake(TransportKind.Bluetooth);
        await using var transport = new SelectingTransport([a, b], new CascadeTransportSelector());

        a.Arrive(NetworkPayload.Create(new byte[] { 1 }));
        b.Arrive(NetworkPayload.Create(new byte[] { 2 }));
        a.Close(); b.Close();

        var got = new List<byte>();
        await foreach (var p in transport.ReceiveAsync()) got.Add(p.Data.Span[0]);
        Assert.Equal(new byte[] { 1, 2 }, got.Order().ToArray());
    }

    [Fact]
    public async Task One_transport_that_will_not_start_does_not_keep_the_rest_down()
    {
        var broken = new Fake(TransportKind.Http) { StartFails = true };
        var fine = new Fake(TransportKind.WiFi);
        await using var transport = new SelectingTransport([broken, fine], new CascadeTransportSelector());

        await transport.StartAsync();
        Assert.True(fine.Started);
    }

    [Fact]
    public async Task Registering_it_puts_every_transport_already_there_behind_the_selector()
    {
        var http = new Fake(TransportKind.Http) { Fails = true };
        var wifi = new Fake(TransportKind.WiFi);
        var services = new ServiceCollection()
            .AddSingleton<INetworkTransport>(http)
            .AddSingleton<INetworkTransport>(wifi)
            .AddSelectingTransport();

        await using var provider = services.BuildServiceProvider();
        var transport = Assert.IsType<SelectingTransport>(Assert.Single(provider.GetServices<INetworkTransport>()));
        Assert.IsType<CascadeTransportSelector>(provider.GetRequiredService<ITransportSelector>());

        await transport.SendAsync(Plain);
        Assert.Single(wifi.Sent);
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private sealed class Rules : INetworkPolicy
    {
        public TransportKind? Force { get; init; }
        public bool Mesh { get; init; }
        public bool Cloud { get; init; } = true;
        public bool Queue { get; init; } = true;
        public TransportKind? Refuse { get; init; }

        public bool Permits(TransportKind transport, NetworkPayload payload) => transport != Refuse;
        public TransportKind? ForceTransport => Force;
        public bool MeshFirst => Mesh;
        public bool OfflineQueueEnabled => Queue;
        public bool AllowCloudTransports => Cloud;
    }

    private sealed class Fake(TransportKind kind) : INetworkTransport
    {
        private readonly Channel<NetworkPayload> _inbox = Channel.CreateUnbounded<NetworkPayload>();

        public TransportKind Kind => kind;
        public bool Up { get; init; } = true;
        public bool IsAvailable => Up;
        public bool Fails { get; init; }
        public bool StartFails { get; init; }
        public bool Started { get; private set; }
        public List<NetworkPayload> Tried { get; } = [];
        public List<NetworkPayload> Sent { get; } = [];

        public Task StartAsync(CancellationToken ct = default)
        {
            if (StartFails) throw new InvalidOperationException("radio off");
            Started = true;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task SendAsync(NetworkPayload payload, CancellationToken ct = default)
        {
            Tried.Add(payload);
            if (Fails) throw new InvalidOperationException($"{kind} refused");
            Sent.Add(payload);
            return Task.CompletedTask;
        }

        public void Arrive(NetworkPayload p) => _inbox.Writer.TryWrite(p);
        public void Close() => _inbox.Writer.TryComplete();

        public async IAsyncEnumerable<NetworkPayload> ReceiveAsync([EnumeratorCancellation] CancellationToken ct = default)
        {
            await foreach (var p in _inbox.Reader.ReadAllAsync(ct)) yield return p;
        }
    }
}
