// Circle33TelnyxCarrierTests.cs
//
// (3.3.0) Tests for CircleAI.Telephony.Telnyx — REST adapter against
// a fake HttpMessageHandler. No live Telnyx calls.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Telephony;
using CircleAI.Telephony.Telnyx;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CircleAI.Tests;

public class Circle33TelnyxCarrierTests
{
    private const string FakeApiKey       = "KEY_test_telnyx_abcdef1234567890";
    private const string FakeConnectionId = "2010000000001";

    private static (TelnyxCarrier carrier, TelnyxRecordingHandler handler) NewCarrier(
        params (Func<HttpRequestMessage, bool> Match, HttpResponseMessage Response)[] responses)
    {
        var handler = new TelnyxRecordingHandler(responses);
        var http = new HttpClient(handler);
        var options = new TelnyxOptions
        {
            ApiKey                  = FakeApiKey,
            CallControlConnectionId = FakeConnectionId,
        };
        return (new TelnyxCarrier(http, options), handler);
    }

    [Fact]
    public void Carrier_IsConfigured_FalseWhenKeyMissing()
    {
        var http = new HttpClient(new TelnyxRecordingHandler());
        var carrier = new TelnyxCarrier(http, new TelnyxOptions());
        Assert.False(carrier.IsConfigured);
    }

    [Fact]
    public void Carrier_IsConfigured_TrueWhenKeySet()
    {
        var (carrier, _) = NewCarrier();
        Assert.True(carrier.IsConfigured);
    }

    [Fact]
    public void Carrier_CarrierId_IsTelnyx()
    {
        var (carrier, _) = NewCarrier();
        Assert.Equal("telnyx", carrier.CarrierId);
    }

    [Fact]
    public async Task ProvisionNumber_Throws_WhenNotConfigured()
    {
        var http = new HttpClient(new TelnyxRecordingHandler());
        var carrier = new TelnyxCarrier(http, new TelnyxOptions());
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => carrier.ProvisionNumberAsync("US").AsTask());
    }

    [Fact]
    public async Task Dial_Throws_WhenNotConfigured()
    {
        var http = new HttpClient(new TelnyxRecordingHandler());
        var carrier = new TelnyxCarrier(http, new TelnyxOptions());
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => carrier.DialAsync("+15555550100", "+15555550200", new Uri("wss://example.com/stream")).AsTask());
    }

    [Fact]
    public async Task Dial_Throws_WhenConnectionIdMissing()
    {
        var http = new HttpClient(new TelnyxRecordingHandler());
        var carrier = new TelnyxCarrier(http, new TelnyxOptions { ApiKey = FakeApiKey });
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => carrier.DialAsync("+15555550100", "+15555550200", new Uri("wss://example.com/stream")).AsTask());
    }

    [Fact]
    public async Task ConfigureInboundWebhook_Throws_WhenConnectionIdMissing()
    {
        var http = new HttpClient(new TelnyxRecordingHandler());
        var carrier = new TelnyxCarrier(http, new TelnyxOptions { ApiKey = FakeApiKey });
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => carrier.ConfigureInboundWebhookAsync("+15555550100", new Uri("https://example.com/voice")).AsTask());
    }

    [Fact]
    public async Task ListNumbers_ReturnsEmpty_WhenNotConfigured()
    {
        var http = new HttpClient(new TelnyxRecordingHandler());
        var carrier = new TelnyxCarrier(http, new TelnyxOptions());
        var list = await carrier.ListNumbersAsync();
        Assert.Empty(list);
    }

    [Fact]
    public async Task ProvisionNumber_SearchesThenOrdersNumber()
    {
        var (carrier, handler) = NewCarrier(
            (r => r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath.Contains("available_phone_numbers"),
             Json("""{"data":[{"phone_number":"+15555550199","cost_information":{"monthly_cost":"1.00"}}]}""")),
            (r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.EndsWith("/v2/number_orders"),
             Json("""{"data":{"id":"order-1"}}""")));

        var provisioned = await carrier.ProvisionNumberAsync("US");

        Assert.Equal("+15555550199", provisioned.PhoneNumber);
        Assert.Equal("telnyx", provisioned.CarrierId);
        Assert.Equal(1.00m, provisioned.MonthlyRecurringCost);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task ProvisionNumber_PassesAreaCode()
    {
        var (carrier, handler) = NewCarrier(
            (r => r.Method == HttpMethod.Get, Json("""{"data":[{"phone_number":"+14155550100"}]}""")),
            (r => r.Method == HttpMethod.Post, Json("""{"data":{"id":"order-x"}}""")));

        await carrier.ProvisionNumberAsync("US", areaCode: "415");

        var fullUrl = Uri.UnescapeDataString(handler.Requests[0].RequestUri!.AbsoluteUri);
        Assert.Contains("national_destination_code", fullUrl);
        Assert.Contains("415", fullUrl);
    }

    [Fact]
    public async Task ProvisionNumber_ThrowsWhenNoneAvailable()
    {
        var (carrier, _) = NewCarrier((_ => true, Json("""{"data":[]}""")));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => carrier.ProvisionNumberAsync("US").AsTask());
    }

    [Fact]
    public async Task ConfigureInboundWebhook_UpdatesAppAndAssignsNumber()
    {
        var (carrier, handler) = NewCarrier(
            (r => r.Method == HttpMethod.Patch && r.RequestUri!.AbsolutePath.Contains("/call_control_applications/"),
             Json("""{"data":{"id":"2010000000001"}}""")),
            (r => r.Method == HttpMethod.Patch && r.RequestUri!.AbsolutePath.Contains("/phone_numbers/"),
             Json("""{"data":{"phone_number":"+15555550100"}}""")));

        await carrier.ConfigureInboundWebhookAsync("+15555550100", new Uri("https://example.com/telnyx/voice"));

        Assert.Equal(2, handler.Bodies.Count);
        Assert.Contains("webhook_event_url", handler.Bodies[0]);
        Assert.Contains("https://example.com/telnyx/voice", handler.Bodies[0]);

        Assert.Contains("connection_id", handler.Bodies[1]);
        Assert.Contains(FakeConnectionId, handler.Bodies[1]);
    }

    [Fact]
    public async Task Dial_PostsCallControlBody()
    {
        var (carrier, handler) = NewCarrier(
            (_ => true, Json("""{"data":{"call_control_id":"AAAA1234","call_session_id":"SS1"}}""")));

        var session = await carrier.DialAsync(
            fromNumber: "+15555550100",
            toNumber:   "+15555550200",
            streamUrl:  new Uri("wss://example.com/stream"));

        Assert.Equal("AAAA1234", session.Info.CallId);
        Assert.Equal(CallDirection.Outbound, session.Info.Direction);
        Assert.Equal("telnyx", session.Info.CarrierId);

        Assert.Contains($"\"connection_id\":\"{FakeConnectionId}\"", handler.Bodies[0]);
        Assert.Contains("\"to\":\"+15555550200\"", handler.Bodies[0]);
        Assert.Contains("\"from\":\"+15555550100\"", handler.Bodies[0]);
        Assert.Contains("\"stream_url\":\"wss://example.com/stream\"", handler.Bodies[0]);
        Assert.Contains("\"stream_track\":\"both_tracks\"", handler.Bodies[0]);
    }

    [Fact]
    public async Task Dial_HonoursCallerIdOverride()
    {
        var (carrier, handler) = NewCarrier(
            (_ => true, Json("""{"data":{"call_control_id":"X9"}}""")));

        await carrier.DialAsync(
            fromNumber: "+15555550100",
            toNumber:   "+15555550200",
            streamUrl:  new Uri("wss://example.com/stream"),
            options:    new OutboundDialOptions { CallerIdOverride = "+18005551212" });

        Assert.Contains("\"from\":\"+18005551212\"", handler.Bodies[0]);
    }

    [Fact]
    public async Task Dial_SetsAnsweringMachineDetectionWhenEnabled()
    {
        var (carrier, handler) = NewCarrier(
            (_ => true, Json("""{"data":{"call_control_id":"AMD1"}}""")));

        await carrier.DialAsync(
            fromNumber: "+15555550100",
            toNumber:   "+15555550200",
            streamUrl:  new Uri("wss://example.com/stream"),
            options:    new OutboundDialOptions { DetectAnsweringMachine = true });

        Assert.Contains("\"answering_machine_detection\":\"detect\"", handler.Bodies[0]);
    }

    [Fact]
    public async Task ListNumbers_ParsesResponse()
    {
        var (carrier, _) = NewCarrier(
            (_ => true, Json("""{"data":[{"phone_number":"+15555550100"},{"phone_number":"+15555550200"}]}""")));

        var list = await carrier.ListNumbersAsync();

        Assert.Equal(2, list.Count);
        Assert.All(list, n => Assert.Equal("telnyx", n.CarrierId));
    }

    [Fact]
    public async Task ListNumbers_ReturnsEmptyOnNon200()
    {
        var (carrier, _) = NewCarrier(
            (_ => true, new HttpResponseMessage(HttpStatusCode.Unauthorized)));

        var list = await carrier.ListNumbersAsync();
        Assert.Empty(list);
    }

    [Fact]
    public async Task Session_HangUp_PostsHangupAction()
    {
        var (carrier, handler) = NewCarrier(
            (r => r.RequestUri!.AbsolutePath == "/v2/calls",
             Json("""{"data":{"call_control_id":"HG1"}}""")),
            (r => r.RequestUri!.AbsolutePath.EndsWith("/HG1/actions/hangup"),
             Json("""{"data":{"result":"ok"}}""")));

        var session = await carrier.DialAsync("+15555550100", "+15555550200", new Uri("wss://example.com/stream"));
        await session.HangUpAsync();

        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("/actions/hangup", handler.Requests[1].RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task Session_ColdTransfer_PostsTransferAction()
    {
        var (carrier, handler) = NewCarrier(
            (r => r.RequestUri!.AbsolutePath == "/v2/calls",
             Json("""{"data":{"call_control_id":"TR1"}}""")),
            (r => r.RequestUri!.AbsolutePath.EndsWith("/TR1/actions/transfer"),
             Json("""{"data":{"result":"ok"}}""")));

        var session = await carrier.DialAsync("+15555550100", "+15555550200", new Uri("wss://example.com/stream"));
        await session.TransferAsync("+18005550199", TransferMode.Cold);

        Assert.Equal(CallStatus.Transferred, session.Status);
        Assert.Contains("\"to\":\"+18005550199\"", handler.Bodies[1]);
    }

    [Fact]
    public async Task Session_WarmTransfer_FallsThroughToColdWhenNoBriefingTtsConfigured()
    {
        // Without a BriefingSynthesiser the session can't speak the briefing
        // to the target leg, so warm degrades to cold transfer (the caller
        // still reaches a human) rather than throwing.
        var (carrier, _) = NewCarrier(
            (_ => true, Json("""{"data":{"call_control_id":"WT"}}""")));

        var session = await carrier.DialAsync("+15555550100", "+15555550200", new Uri("wss://example.com/stream"));
        await session.TransferAsync("+18005550199", TransferMode.Warm);
        Assert.Equal(CallStatus.Transferred, session.Status);
    }

    [Fact]
    public async Task Session_StatusChanged_FiresOnHangUp()
    {
        var (carrier, _) = NewCarrier(
            (r => r.RequestUri!.AbsolutePath == "/v2/calls",
             Json("""{"data":{"call_control_id":"SC1"}}""")),
            (r => r.RequestUri!.AbsolutePath.EndsWith("/actions/hangup"),
             Json("""{"data":{}}""")));

        var session = await carrier.DialAsync("+15555550100", "+15555550200", new Uri("wss://example.com/stream"));
        CallStatus? observed = null;
        session.StatusChanged += (_, s) => observed = s;

        await session.HangUpAsync();

        Assert.Equal(CallStatus.EndedByAgent, observed);
    }

    [Fact]
    public void DI_AddTelnyxCarrier_RegistersCarrierAsITelephonyCarrier()
    {
        var services = new ServiceCollection();
        services.AddTelnyxCarrier(_ => new TelnyxOptions { ApiKey = FakeApiKey, CallControlConnectionId = FakeConnectionId });
        using var sp = services.BuildServiceProvider();

        var carrier = sp.GetRequiredService<ITelephonyCarrier>();
        Assert.IsType<TelnyxCarrier>(carrier);
        Assert.Equal("telnyx", carrier.CarrierId);
    }

    [Fact]
    public async Task Session_SendDtmf_FallsBackToInBandAndPendingStreamReportsAttachNeeded()
    {
        var (carrier, _) = NewCarrier(
            (_ => true, Json("""{"data":{"call_control_id":"DTMF1"}}""")));

        var session = await carrier.DialAsync("+15555550100", "+15555550200", new Uri("wss://example.com/stream"));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => session.SendDtmfAsync("1234").AsTask());
        Assert.Contains("WebSocket has attached", ex.Message);
    }

    [Fact]
    public async Task Session_SendAudio_BeforeAttach_Throws()
    {
        var (carrier, _) = NewCarrier(
            (_ => true, Json("""{"data":{"call_control_id":"SA1"}}""")));

        var session = await carrier.DialAsync("+15555550100", "+15555550200", new Uri("wss://example.com/stream"));
        var frame = new AudioFrame(new byte[160], CallMediaFormat.Pcm16000, TimeSpan.Zero);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.SendAudioAsync(frame).AsTask());
    }

    [Fact]
    public async Task Carrier_AttachesBearerAuthHeader()
    {
        var (carrier, handler) = NewCarrier(
            (_ => true, Json("""{"data":[]}""")));

        await carrier.ListNumbersAsync();

        var auth = handler.Requests[0].Headers.Authorization;
        Assert.NotNull(auth);
        Assert.Equal("Bearer", auth!.Scheme);
        Assert.Equal(FakeApiKey, auth.Parameter);
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class TelnyxRecordingHandler : HttpMessageHandler
    {
        private readonly List<(Func<HttpRequestMessage, bool> Match, HttpResponseMessage Response)> _responses;
        public List<HttpRequestMessage> Requests { get; } = new();
        /// <summary>Captured request bodies (eagerly read so callers can assert after the request disposes).</summary>
        public List<string> Bodies { get; } = new();

        public TelnyxRecordingHandler(params (Func<HttpRequestMessage, bool>, HttpResponseMessage)[] responses)
        {
            _responses = responses.ToList();
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));

            for (int i = 0; i < _responses.Count; i++)
            {
                if (_responses[i].Match(request))
                {
                    var resp = _responses[i].Response;
                    _responses.RemoveAt(i);
                    return resp;
                }
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent($"No fake response registered for {request.Method} {request.RequestUri}"),
            };
        }
    }
}
