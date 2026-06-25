// Circle33TwilioCarrierTests.cs
//
// (3.3.0) Tests for CircleAI.Telephony.Twilio — REST adapter against
// a fake HttpMessageHandler that records every request and replays
// canned responses. No live Twilio calls.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Telephony;
using CircleAI.Telephony.Twilio;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CircleAI.Tests;

public class Circle33TwilioCarrierTests
{
    private const string FakeAccountSid = "ACtest1234567890abcdef1234567890ab";
    private const string FakeAuthToken  = "test_auth_token_value_xyz";

    private static (TwilioCarrier carrier, RecordingHandler handler) NewCarrier(
        params (Func<HttpRequestMessage, bool> Match, HttpResponseMessage Response)[] responses)
    {
        var handler = new RecordingHandler(responses);
        var http = new HttpClient(handler);
        var options = new TwilioOptions { AccountSid = FakeAccountSid, AuthToken = FakeAuthToken };
        return (new TwilioCarrier(http, options), handler);
    }

    [Fact]
    public void Carrier_IsConfigured_FalseWhenSidMissing()
    {
        var http = new HttpClient(new RecordingHandler());
        var carrier = new TwilioCarrier(http, new TwilioOptions { AccountSid = null, AuthToken = "x" });
        Assert.False(carrier.IsConfigured);
    }

    [Fact]
    public void Carrier_IsConfigured_FalseWhenTokenMissing()
    {
        var http = new HttpClient(new RecordingHandler());
        var carrier = new TwilioCarrier(http, new TwilioOptions { AccountSid = "ACx", AuthToken = null });
        Assert.False(carrier.IsConfigured);
    }

    [Fact]
    public void Carrier_IsConfigured_TrueWhenBothSet()
    {
        var (carrier, _) = NewCarrier();
        Assert.True(carrier.IsConfigured);
    }

    [Fact]
    public void Carrier_CarrierId_IsTwilio()
    {
        var (carrier, _) = NewCarrier();
        Assert.Equal("twilio", carrier.CarrierId);
    }

    [Fact]
    public async Task ProvisionNumber_Throws_WhenNotConfigured()
    {
        var http = new HttpClient(new RecordingHandler());
        var carrier = new TwilioCarrier(http, new TwilioOptions());
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => carrier.ProvisionNumberAsync("US").AsTask());
    }

    [Fact]
    public async Task ConfigureInboundWebhook_Throws_WhenNotConfigured()
    {
        var http = new HttpClient(new RecordingHandler());
        var carrier = new TwilioCarrier(http, new TwilioOptions());
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => carrier.ConfigureInboundWebhookAsync("+15555550100", new Uri("https://example.com/voice")).AsTask());
    }

    [Fact]
    public async Task Dial_Throws_WhenNotConfigured()
    {
        var http = new HttpClient(new RecordingHandler());
        var carrier = new TwilioCarrier(http, new TwilioOptions());
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => carrier.DialAsync("+15555550100", "+15555550200", new Uri("wss://example.com/stream")).AsTask());
    }

    [Fact]
    public async Task ListNumbers_ReturnsEmpty_WhenNotConfigured()
    {
        var http = new HttpClient(new RecordingHandler());
        var carrier = new TwilioCarrier(http, new TwilioOptions());
        var result = await carrier.ListNumbersAsync();
        Assert.Empty(result);
    }

    [Fact]
    public async Task ProvisionNumber_RoundtripsAvailableAndReserves()
    {
        var (carrier, handler) = NewCarrier(
            (r => r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath.Contains("AvailablePhoneNumbers"),
             Json("""{"available_phone_numbers":[{"phone_number":"+15555550199","price":"1.15"}]}""")),
            (r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.EndsWith("IncomingPhoneNumbers.json"),
             Json("""{"sid":"PNxxx","phone_number":"+15555550199"}""")));

        var provisioned = await carrier.ProvisionNumberAsync("US");

        Assert.Equal("+15555550199", provisioned.PhoneNumber);
        Assert.Equal("twilio", provisioned.CarrierId);
        Assert.Equal(1.15m, provisioned.MonthlyRecurringCost);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task ProvisionNumber_PassesAreaCodeQueryParam()
    {
        var (carrier, handler) = NewCarrier(
            (r => r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath.Contains("AvailablePhoneNumbers"),
             Json("""{"available_phone_numbers":[{"phone_number":"+14155550199","price":1.10}]}""")),
            (r => r.Method == HttpMethod.Post,
             Json("""{"sid":"PN1"}""")));

        await carrier.ProvisionNumberAsync("US", areaCode: "415");

        Assert.Contains("AreaCode=415", handler.Requests[0].RequestUri!.Query);
    }

    [Fact]
    public async Task ProvisionNumber_ThrowsWhenNoneAvailable()
    {
        var (carrier, _) = NewCarrier(
            (_ => true, Json("""{"available_phone_numbers":[]}""")));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => carrier.ProvisionNumberAsync("US").AsTask());
    }

    [Fact]
    public async Task ConfigureInboundWebhook_PostsVoiceUrl()
    {
        var (carrier, handler) = NewCarrier(
            (r => r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath.EndsWith("IncomingPhoneNumbers.json"),
             Json("""{"incoming_phone_numbers":[{"sid":"PNabc","phone_number":"+15555550100"}]}""")),
            (r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.Contains("/IncomingPhoneNumbers/PNabc"),
             Json("""{"sid":"PNabc"}""")));

        await carrier.ConfigureInboundWebhookAsync("+15555550100", new Uri("https://example.com/twilio/voice"));

        var postBody = await handler.Requests[1].Content!.ReadAsStringAsync();
        Assert.Contains("VoiceUrl=https", postBody);
        Assert.Contains("twilio%2Fvoice", postBody);
    }

    [Fact]
    public async Task ConfigureInboundWebhook_ThrowsWhenNumberNotOwned()
    {
        var (carrier, _) = NewCarrier(
            (_ => true, Json("""{"incoming_phone_numbers":[]}""")));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => carrier.ConfigureInboundWebhookAsync("+15555550100", new Uri("https://example.com/voice")).AsTask());
    }

    [Fact]
    public async Task Dial_PostsTwiMLWithStreamUrl()
    {
        var (carrier, handler) = NewCarrier(
            (_ => true, Json("""{"sid":"CAabcd1234","status":"queued"}""")));

        var session = await carrier.DialAsync(
            fromNumber: "+15555550100",
            toNumber:   "+15555550200",
            streamUrl:  new Uri("wss://example.com/stream"));

        Assert.Equal("CAabcd1234", session.Info.CallId);
        Assert.Equal(CallDirection.Outbound, session.Info.Direction);
        Assert.Equal("+15555550100", session.Info.From);
        Assert.Equal("+15555550200", session.Info.To);
        Assert.Equal("twilio", session.Info.CarrierId);

        var postBody = await handler.Requests[0].Content!.ReadAsStringAsync();
        Assert.Contains("From=%2B15555550100", postBody);
        Assert.Contains("To=%2B15555550200", postBody);
        Assert.Contains("Twiml=", postBody);
        Assert.Contains("wss", postBody);
    }

    [Fact]
    public async Task Dial_HonoursCallerIdOverride()
    {
        var (carrier, handler) = NewCarrier(
            (_ => true, Json("""{"sid":"CA9","status":"queued"}""")));

        await carrier.DialAsync(
            fromNumber: "+15555550100",
            toNumber:   "+15555550200",
            streamUrl:  new Uri("wss://example.com/stream"),
            options:    new OutboundDialOptions { CallerIdOverride = "+18005551212" });

        var postBody = await handler.Requests[0].Content!.ReadAsStringAsync();
        Assert.Contains("From=%2B18005551212", postBody);
        Assert.DoesNotContain("From=%2B15555550100", postBody);
    }

    [Fact]
    public async Task Dial_SetsMachineDetectionWhenEnabled()
    {
        var (carrier, handler) = NewCarrier(
            (_ => true, Json("""{"sid":"CA9","status":"queued"}""")));

        await carrier.DialAsync(
            fromNumber: "+15555550100",
            toNumber:   "+15555550200",
            streamUrl:  new Uri("wss://example.com/stream"),
            options:    new OutboundDialOptions { DetectAnsweringMachine = true });

        var postBody = await handler.Requests[0].Content!.ReadAsStringAsync();
        Assert.Contains("MachineDetection=Enable", postBody);
    }

    [Fact]
    public async Task ListNumbers_ParsesResponse()
    {
        var (carrier, _) = NewCarrier(
            (_ => true, Json("""{"incoming_phone_numbers":[{"phone_number":"+15555550100"},{"phone_number":"+15555550200"}]}""")));

        var list = await carrier.ListNumbersAsync();

        Assert.Equal(2, list.Count);
        Assert.Equal("+15555550100", list[0].PhoneNumber);
        Assert.Equal("+15555550200", list[1].PhoneNumber);
        Assert.All(list, n => Assert.Equal("twilio", n.CarrierId));
    }

    [Fact]
    public async Task ListNumbers_ReturnsEmptyOnNon200()
    {
        var (carrier, _) = NewCarrier(
            (_ => true, new HttpResponseMessage(HttpStatusCode.InternalServerError)));

        var list = await carrier.ListNumbersAsync();
        Assert.Empty(list);
    }

    [Fact]
    public async Task Session_HangUp_PostsCompleted()
    {
        var (carrier, handler) = NewCarrier(
            (r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.EndsWith("Calls.json"),
             Json("""{"sid":"CAxyz"}""")),
            (r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.Contains("/Calls/CAxyz.json"),
             Json("""{"status":"completed"}""")));

        var session = await carrier.DialAsync("+15555550100", "+15555550200", new Uri("wss://example.com/stream"));
        await session.HangUpAsync();

        var hangUpBody = await handler.Requests[1].Content!.ReadAsStringAsync();
        Assert.Contains("Status=completed", hangUpBody);
    }

    [Fact]
    public async Task Session_ColdTransfer_PostsRedirectTwiml()
    {
        var (carrier, handler) = NewCarrier(
            (r => r.RequestUri!.AbsolutePath.EndsWith("Calls.json"),
             Json("""{"sid":"CAtransfer"}""")),
            (r => r.RequestUri!.AbsolutePath.Contains("/Calls/CAtransfer.json"),
             Json("""{"status":"in-progress"}""")));

        var session = await carrier.DialAsync("+15555550100", "+15555550200", new Uri("wss://example.com/stream"));
        await session.TransferAsync("+18005550199", TransferMode.Cold);

        Assert.Equal(CallStatus.Transferred, session.Status);
        var redirectBody = await handler.Requests[1].Content!.ReadAsStringAsync();
        Assert.Contains("Twiml=", redirectBody);
        Assert.Contains("Dial", redirectBody);
    }

    [Fact]
    public async Task Session_WarmTransfer_FallsThroughToColdWhenNoBriefingTtsConfigured()
    {
        // Without a BriefingSynthesiser the session can't speak the briefing
        // to the target leg, so warm degrades to cold transfer (the caller
        // still reaches a human) rather than throwing.
        var (carrier, _) = NewCarrier(
            (_ => true, Json("""{"sid":"CAwarm"}""")));

        var session = await carrier.DialAsync("+15555550100", "+15555550200", new Uri("wss://example.com/stream"));
        await session.TransferAsync("+18005550199", TransferMode.Warm);
        Assert.Equal(CallStatus.Transferred, session.Status);
    }

    [Fact]
    public async Task Session_StatusChanged_FiresOnHangUp()
    {
        var (carrier, _) = NewCarrier(
            (r => r.RequestUri!.AbsolutePath.EndsWith("Calls.json"),
             Json("""{"sid":"CAhang"}""")),
            (r => r.RequestUri!.AbsolutePath.Contains("/Calls/CAhang.json"),
             Json("""{"status":"completed"}""")));

        var session = await carrier.DialAsync("+15555550100", "+15555550200", new Uri("wss://example.com/stream"));
        CallStatus? observed = null;
        session.StatusChanged += (_, s) => observed = s;

        await session.HangUpAsync();

        Assert.Equal(CallStatus.EndedByAgent, observed);
    }

    [Fact]
    public void DI_AddTwilioCarrier_RegistersCarrierAsITelephonyCarrier()
    {
        var services = new ServiceCollection();
        services.AddTwilioCarrier(_ => new TwilioOptions { AccountSid = FakeAccountSid, AuthToken = FakeAuthToken });
        using var sp = services.BuildServiceProvider();

        var carrier = sp.GetRequiredService<ITelephonyCarrier>();
        Assert.IsType<TwilioCarrier>(carrier);
        Assert.Equal("twilio", carrier.CarrierId);
    }

    [Fact]
    public async Task Session_SendDtmf_FallsBackToInBandAndPendingStreamReportsAttachNeeded()
    {
        // No IDtmfSendable on the media stream → in-band tone generation kicks in.
        // The PendingMediaStream returned before the host attaches its WebSocket
        // refuses outbound audio, so we get an InvalidOperationException naming
        // the missing host wiring — not a NotSupportedException stub.
        var (carrier, _) = NewCarrier(
            (_ => true, Json("""{"sid":"CAdtmf"}""")));

        var session = await carrier.DialAsync("+15555550100", "+15555550200", new Uri("wss://example.com/stream"));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => session.SendDtmfAsync("1234").AsTask());
        Assert.Contains("WebSocket has attached", ex.Message);
    }

    [Fact]
    public async Task Session_SendAudio_BeforeAttach_Throws()
    {
        var (carrier, _) = NewCarrier(
            (_ => true, Json("""{"sid":"CAsend"}""")));

        var session = await carrier.DialAsync("+15555550100", "+15555550200", new Uri("wss://example.com/stream"));
        var frame = new AudioFrame(new byte[160], CallMediaFormat.Mulaw8000, TimeSpan.Zero);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.SendAudioAsync(frame).AsTask());
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>Captures every outgoing request + serves canned responses in registration order.</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly List<(Func<HttpRequestMessage, bool> Match, HttpResponseMessage Response)> _responses;
        public List<HttpRequestMessage> Requests { get; } = new();

        public RecordingHandler(params (Func<HttpRequestMessage, bool>, HttpResponseMessage)[] responses)
        {
            _responses = responses.ToList();
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);

            for (int i = 0; i < _responses.Count; i++)
            {
                if (_responses[i].Match(request))
                {
                    var resp = _responses[i].Response;
                    _responses.RemoveAt(i);
                    return Task.FromResult(resp);
                }
            }

            // No match → 404 so tests fail loudly on unexpected requests.
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent($"No fake response registered for {request.Method} {request.RequestUri}"),
            });
        }
    }
}
