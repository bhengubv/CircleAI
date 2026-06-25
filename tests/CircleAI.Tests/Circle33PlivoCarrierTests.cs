// Circle33PlivoCarrierTests.cs
//
// (3.3.0) Tests for CircleAI.Telephony.Plivo — REST adapter against
// a fake HttpMessageHandler that eagerly captures request bodies.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Telephony;
using CircleAI.Telephony.Plivo;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CircleAI.Tests;

public class Circle33PlivoCarrierTests
{
    private const string FakeAuthId    = "MAtestAuthId1234567890";
    private const string FakeAuthToken = "test_auth_token_plivo_xyz";
    private static readonly Uri FakeAnswerUrlBase = new("https://example.com/plivo/answer");

    private static (PlivoCarrier carrier, PlivoRecordingHandler handler) NewCarrier(
        params (Func<HttpRequestMessage, bool> Match, HttpResponseMessage Response)[] responses)
    {
        var handler = new PlivoRecordingHandler(responses);
        var http = new HttpClient(handler);
        var options = new PlivoOptions
        {
            AuthId        = FakeAuthId,
            AuthToken     = FakeAuthToken,
            AnswerUrlBase = FakeAnswerUrlBase,
        };
        return (new PlivoCarrier(http, options), handler);
    }

    [Fact]
    public void Carrier_IsConfigured_FalseWhenAuthIdMissing()
    {
        var http = new HttpClient(new PlivoRecordingHandler());
        var carrier = new PlivoCarrier(http, new PlivoOptions { AuthId = null, AuthToken = "x" });
        Assert.False(carrier.IsConfigured);
    }

    [Fact]
    public void Carrier_IsConfigured_FalseWhenTokenMissing()
    {
        var http = new HttpClient(new PlivoRecordingHandler());
        var carrier = new PlivoCarrier(http, new PlivoOptions { AuthId = "MAx", AuthToken = null });
        Assert.False(carrier.IsConfigured);
    }

    [Fact]
    public void Carrier_IsConfigured_TrueWhenBothSet()
    {
        var (carrier, _) = NewCarrier();
        Assert.True(carrier.IsConfigured);
    }

    [Fact]
    public void Carrier_CarrierId_IsPlivo()
    {
        var (carrier, _) = NewCarrier();
        Assert.Equal("plivo", carrier.CarrierId);
    }

    [Fact]
    public async Task ProvisionNumber_Throws_WhenNotConfigured()
    {
        var http = new HttpClient(new PlivoRecordingHandler());
        var carrier = new PlivoCarrier(http, new PlivoOptions());
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => carrier.ProvisionNumberAsync("US").AsTask());
    }

    [Fact]
    public async Task Dial_Throws_WhenNotConfigured()
    {
        var http = new HttpClient(new PlivoRecordingHandler());
        var carrier = new PlivoCarrier(http, new PlivoOptions());
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => carrier.DialAsync("+15555550100", "+15555550200", new Uri("wss://example.com/stream")).AsTask());
    }

    [Fact]
    public async Task Dial_Throws_WhenAnswerUrlBaseMissing()
    {
        var http = new HttpClient(new PlivoRecordingHandler());
        var carrier = new PlivoCarrier(http, new PlivoOptions { AuthId = FakeAuthId, AuthToken = FakeAuthToken });
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => carrier.DialAsync("+15555550100", "+15555550200", new Uri("wss://example.com/stream")).AsTask());
    }

    [Fact]
    public async Task ListNumbers_ReturnsEmpty_WhenNotConfigured()
    {
        var http = new HttpClient(new PlivoRecordingHandler());
        var carrier = new PlivoCarrier(http, new PlivoOptions());
        var list = await carrier.ListNumbersAsync();
        Assert.Empty(list);
    }

    [Fact]
    public async Task ProvisionNumber_SearchesThenBuysNumber()
    {
        var (carrier, handler) = NewCarrier(
            (r => r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath.Contains("/PhoneNumber/"),
             Json("""{"objects":[{"number":"+15555550199","monthly_rental_rate":"0.80"}]}""")),
            (r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.Contains("/PhoneNumber/+15555550199/"),
             Json("""{"message":"created"}""")));

        var provisioned = await carrier.ProvisionNumberAsync("US");

        Assert.Equal("+15555550199", provisioned.PhoneNumber);
        Assert.Equal("plivo", provisioned.CarrierId);
        Assert.Equal(0.80m, provisioned.MonthlyRecurringCost);
    }

    [Fact]
    public async Task ProvisionNumber_PassesPattern()
    {
        var (carrier, handler) = NewCarrier(
            (r => r.Method == HttpMethod.Get,
             Json("""{"objects":[{"number":"+14155550100"}]}""")),
            (r => r.Method == HttpMethod.Post,
             Json("""{"message":"ok"}""")));

        await carrier.ProvisionNumberAsync("US", areaCode: "415");

        var fullUrl = Uri.UnescapeDataString(handler.Requests[0].RequestUri!.AbsoluteUri);
        Assert.Contains("pattern", fullUrl);
        Assert.Contains("415", fullUrl);
    }

    [Fact]
    public async Task ProvisionNumber_ThrowsWhenNoneAvailable()
    {
        var (carrier, _) = NewCarrier((_ => true, Json("""{"objects":[]}""")));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => carrier.ProvisionNumberAsync("US").AsTask());
    }

    [Fact]
    public async Task ConfigureInboundWebhook_PostsAnswerUrl()
    {
        var (carrier, handler) = NewCarrier(
            (_ => true, Json("""{"message":"updated"}""")));

        await carrier.ConfigureInboundWebhookAsync("+15555550100", new Uri("https://example.com/voice"));

        Assert.Single(handler.Bodies);
        Assert.Contains("answer_url", handler.Bodies[0]);
        Assert.Contains("answer_method", handler.Bodies[0]);
        var path = Uri.UnescapeDataString(handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Contains("/Number/+15555550100/", path);
    }

    [Fact]
    public async Task Dial_BuildsAnswerUrlWithStreamParam()
    {
        var (carrier, handler) = NewCarrier(
            (_ => true, Json("""{"request_uuid":"req-1234","message":"call fired"}""")));

        var session = await carrier.DialAsync(
            fromNumber: "+15555550100",
            toNumber:   "+15555550200",
            streamUrl:  new Uri("wss://example.com/stream"));

        Assert.Equal("req-1234", session.Info.CallId);
        Assert.Equal("plivo", session.Info.CarrierId);

        // The dial form sent to Plivo contains an answer_url with stream=wss://...
        // The stream URL is escaped twice (once into the query, once into the form body),
        // so we unescape twice before asserting.
        var decoded = Uri.UnescapeDataString(Uri.UnescapeDataString(handler.Bodies[0]));
        Assert.Contains("from=+15555550100", decoded);
        Assert.Contains("to=+15555550200", decoded);
        Assert.Contains("answer_url=", decoded);
        Assert.Contains("stream=wss://example.com/stream", decoded);
    }

    [Fact]
    public async Task Dial_HonoursCallerIdOverride()
    {
        var (carrier, handler) = NewCarrier(
            (_ => true, Json("""{"request_uuid":"req-x"}""")));

        await carrier.DialAsync(
            fromNumber: "+15555550100",
            toNumber:   "+15555550200",
            streamUrl:  new Uri("wss://example.com/stream"),
            options:    new OutboundDialOptions { CallerIdOverride = "+18005551212" });

        var decoded = Uri.UnescapeDataString(handler.Bodies[0]);
        Assert.Contains("from=+18005551212", decoded);
        Assert.DoesNotContain("from=+15555550100", decoded);
    }

    [Fact]
    public async Task Dial_SetsMachineDetectionWhenEnabled()
    {
        var (carrier, handler) = NewCarrier(
            (_ => true, Json("""{"request_uuid":"req-amd"}""")));

        await carrier.DialAsync(
            fromNumber: "+15555550100",
            toNumber:   "+15555550200",
            streamUrl:  new Uri("wss://example.com/stream"),
            options:    new OutboundDialOptions { DetectAnsweringMachine = true });

        Assert.Contains("machine_detection=true", handler.Bodies[0]);
    }

    [Fact]
    public async Task ListNumbers_ParsesResponse()
    {
        var (carrier, _) = NewCarrier(
            (_ => true, Json("""{"objects":[{"number":"+15555550100"},{"number":"+15555550200"}]}""")));

        var list = await carrier.ListNumbersAsync();
        Assert.Equal(2, list.Count);
        Assert.All(list, n => Assert.Equal("plivo", n.CarrierId));
    }

    [Fact]
    public async Task ListNumbers_ReturnsEmptyOnNon200()
    {
        var (carrier, _) = NewCarrier(
            (_ => true, new HttpResponseMessage(HttpStatusCode.Forbidden)));

        var list = await carrier.ListNumbersAsync();
        Assert.Empty(list);
    }

    [Fact]
    public async Task Session_HangUp_SendsDelete()
    {
        var (carrier, handler) = NewCarrier(
            (r => r.RequestUri!.AbsolutePath.EndsWith("/Call/"),
             Json("""{"request_uuid":"req-hg","message":"call fired"}""")),
            (r => r.Method == HttpMethod.Delete && r.RequestUri!.AbsolutePath.Contains("/Call/req-hg/"),
             Json("""{"message":"hung up"}""")));

        var session = await carrier.DialAsync("+15555550100", "+15555550200", new Uri("wss://example.com/stream"));
        await session.HangUpAsync();

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(HttpMethod.Delete, handler.Requests[1].Method);
    }

    [Fact]
    public async Task Session_ColdTransfer_SetsStatus()
    {
        var (carrier, _) = NewCarrier(
            (r => r.RequestUri!.AbsolutePath.EndsWith("/Call/"),
             Json("""{"request_uuid":"req-tr"}""")),
            (r => r.RequestUri!.AbsolutePath.Contains("/Call/req-tr/"),
             Json("""{"message":"transferred"}""")));

        var session = await carrier.DialAsync("+15555550100", "+15555550200", new Uri("wss://example.com/stream"));
        await session.TransferAsync("+18005550199", TransferMode.Cold);

        Assert.Equal(CallStatus.Transferred, session.Status);
    }

    [Fact]
    public async Task Session_WarmTransfer_FallsThroughToColdWhenNoBriefingTtsConfigured()
    {
        // Without a BriefingSynthesiser the session can't speak the briefing
        // to the target leg, so warm degrades to cold transfer (the caller
        // still reaches a human) rather than throwing.
        var (carrier, _) = NewCarrier(
            (_ => true, Json("""{"request_uuid":"req-wt"}""")));

        var session = await carrier.DialAsync("+15555550100", "+15555550200", new Uri("wss://example.com/stream"));
        await session.TransferAsync("+18005550199", TransferMode.Warm);
        Assert.Equal(CallStatus.Transferred, session.Status);
    }

    [Fact]
    public async Task Session_StatusChanged_FiresOnHangUp()
    {
        var (carrier, _) = NewCarrier(
            (r => r.RequestUri!.AbsolutePath.EndsWith("/Call/"),
             Json("""{"request_uuid":"req-sc"}""")),
            (r => r.Method == HttpMethod.Delete,
             Json("""{}""")));

        var session = await carrier.DialAsync("+15555550100", "+15555550200", new Uri("wss://example.com/stream"));
        CallStatus? observed = null;
        session.StatusChanged += (_, s) => observed = s;

        await session.HangUpAsync();

        Assert.Equal(CallStatus.EndedByAgent, observed);
    }

    [Fact]
    public void DI_AddPlivoCarrier_RegistersCarrierAsITelephonyCarrier()
    {
        var services = new ServiceCollection();
        services.AddPlivoCarrier(_ => new PlivoOptions
        {
            AuthId        = FakeAuthId,
            AuthToken     = FakeAuthToken,
            AnswerUrlBase = FakeAnswerUrlBase,
        });
        using var sp = services.BuildServiceProvider();

        var carrier = sp.GetRequiredService<ITelephonyCarrier>();
        Assert.IsType<PlivoCarrier>(carrier);
        Assert.Equal("plivo", carrier.CarrierId);
    }

    [Fact]
    public async Task Session_SendDtmf_FallsBackToInBandAndPendingStreamReportsAttachNeeded()
    {
        var (carrier, _) = NewCarrier(
            (_ => true, Json("""{"request_uuid":"req-dt"}""")));

        var session = await carrier.DialAsync("+15555550100", "+15555550200", new Uri("wss://example.com/stream"));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => session.SendDtmfAsync("1234").AsTask());
        Assert.Contains("WebSocket has attached", ex.Message);
    }

    [Fact]
    public async Task Session_SendAudio_BeforeAttach_Throws()
    {
        var (carrier, _) = NewCarrier(
            (_ => true, Json("""{"request_uuid":"req-sa"}""")));

        var session = await carrier.DialAsync("+15555550100", "+15555550200", new Uri("wss://example.com/stream"));
        var frame = new AudioFrame(new byte[160], CallMediaFormat.Mulaw8000, TimeSpan.Zero);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.SendAudioAsync(frame).AsTask());
    }

    [Fact]
    public async Task Carrier_AttachesBasicAuthHeader()
    {
        var (carrier, handler) = NewCarrier(
            (_ => true, Json("""{"objects":[]}""")));

        await carrier.ListNumbersAsync();

        var auth = handler.Requests[0].Headers.Authorization;
        Assert.NotNull(auth);
        Assert.Equal("Basic", auth!.Scheme);
        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(auth.Parameter!));
        Assert.Equal($"{FakeAuthId}:{FakeAuthToken}", decoded);
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class PlivoRecordingHandler : HttpMessageHandler
    {
        private readonly List<(Func<HttpRequestMessage, bool> Match, HttpResponseMessage Response)> _responses;
        public List<HttpRequestMessage> Requests { get; } = new();
        public List<string> Bodies { get; } = new();

        public PlivoRecordingHandler(params (Func<HttpRequestMessage, bool>, HttpResponseMessage)[] responses)
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
                Content = new StringContent($"No fake response for {request.Method} {request.RequestUri}"),
            };
        }
    }
}
