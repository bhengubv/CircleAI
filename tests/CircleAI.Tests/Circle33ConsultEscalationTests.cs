// Circle33ConsultEscalationTests.cs
//
// (3.3.0) Tests for consult escalation.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Telephony;
using Xunit;

namespace CircleAI.Tests;

public class Circle33ConsultEscalationTests
{
    private static readonly ConsultRequest Req = new("call-1", "What's the refund policy?", "{}");

    [Fact]
    public async Task Escalate_FirstChannelAnswers_ReturnsImmediately()
    {
        var ch1 = new FakeChannel("primary", new ConsultAnswer("30 days", Confidence: true));
        var ch2 = new FakeChannel("secondary", new ConsultAnswer("60 days", Confidence: false));
        var esc = new ConsultEscalator(new IConsultChannel[] { ch1, ch2 });

        var r = await esc.EscalateAsync(Req, TimeSpan.FromSeconds(5));

        Assert.NotNull(r);
        Assert.Equal("30 days", r!.Answer);
        Assert.Equal(1, ch1.Calls);
        Assert.Equal(0, ch2.Calls);
    }

    [Fact]
    public async Task Escalate_FirstChannelReturnsNull_FallsBack()
    {
        var ch1 = new FakeChannel("primary", null);
        var ch2 = new FakeChannel("secondary", new ConsultAnswer("answer", Confidence: true));
        var esc = new ConsultEscalator(new IConsultChannel[] { ch1, ch2 });

        var r = await esc.EscalateAsync(Req, TimeSpan.FromSeconds(5));

        Assert.NotNull(r);
        Assert.Equal("answer", r!.Answer);
        Assert.Equal(1, ch1.Calls);
        Assert.Equal(1, ch2.Calls);
    }

    [Fact]
    public async Task Escalate_FirstChannelThrows_FallsBack()
    {
        var ch1 = new FakeChannel("primary", null, throwError: true);
        var ch2 = new FakeChannel("secondary", new ConsultAnswer("answer", Confidence: true));
        var esc = new ConsultEscalator(new IConsultChannel[] { ch1, ch2 });

        var r = await esc.EscalateAsync(Req, TimeSpan.FromSeconds(5));

        Assert.NotNull(r);
        Assert.Equal("answer", r!.Answer);
    }

    [Fact]
    public async Task Escalate_AllChannelsReturnNull_ReturnsNull()
    {
        var esc = new ConsultEscalator(new IConsultChannel[]
        {
            new FakeChannel("a", null),
            new FakeChannel("b", null),
        });

        var r = await esc.EscalateAsync(Req, TimeSpan.FromSeconds(5));
        Assert.Null(r);
    }

    [Fact]
    public async Task HttpWebhookChannel_ParsesAnswer()
    {
        var handler = new ConsultHandler((_ => true,
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"answer":"30 days","confidence":true,"notes":"per policy v3"}""",
                    Encoding.UTF8, "application/json"),
            }));
        var channel = new HttpWebhookConsultChannel(new HttpClient(handler),
            new Uri("https://example.com/consult"));

        var r = await channel.AskAsync(Req, TimeSpan.FromSeconds(5));

        Assert.NotNull(r);
        Assert.Equal("30 days", r!.Answer);
        Assert.True(r.Confidence);
        Assert.Equal("per policy v3", r.Notes);
    }

    [Fact]
    public async Task HttpWebhookChannel_ServerError_ReturnsNull()
    {
        var handler = new ConsultHandler((_ => true, new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        var channel = new HttpWebhookConsultChannel(new HttpClient(handler),
            new Uri("https://example.com/consult"));

        var r = await channel.AskAsync(Req, TimeSpan.FromSeconds(5));
        Assert.Null(r);
    }

    [Fact]
    public async Task HttpWebhookChannel_EmptyAnswer_ReturnsNull()
    {
        var handler = new ConsultHandler((_ => true,
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"answer":"","confidence":true}""",
                    Encoding.UTF8, "application/json"),
            }));
        var channel = new HttpWebhookConsultChannel(new HttpClient(handler),
            new Uri("https://example.com/consult"));

        var r = await channel.AskAsync(Req, TimeSpan.FromSeconds(5));
        Assert.Null(r);
    }

    [Fact]
    public async Task HttpWebhookChannel_Timeout_ReturnsNull()
    {
        var handler = new TimedHandler(TimeSpan.FromMilliseconds(200));
        var channel = new HttpWebhookConsultChannel(new HttpClient(handler),
            new Uri("https://example.com/consult"));

        var r = await channel.AskAsync(Req, TimeSpan.FromMilliseconds(50));
        Assert.Null(r);
    }

    private sealed class FakeChannel : IConsultChannel
    {
        public string Name { get; }
        public int Calls { get; private set; }
        public ConsultAnswer? Answer { get; }
        public bool ThrowError { get; }

        public FakeChannel(string name, ConsultAnswer? answer, bool throwError = false)
        {
            Name = name; Answer = answer; ThrowError = throwError;
        }

        public ValueTask<ConsultAnswer?> AskAsync(ConsultRequest r, TimeSpan timeout, CancellationToken ct = default)
        {
            Calls++;
            if (ThrowError) throw new InvalidOperationException("boom");
            return ValueTask.FromResult(Answer);
        }
    }

    private sealed class ConsultHandler : HttpMessageHandler
    {
        private readonly List<(Func<HttpRequestMessage, bool> Match, HttpResponseMessage Response)> _responses;
        public ConsultHandler(params (Func<HttpRequestMessage, bool>, HttpResponseMessage)[] responses)
        { _responses = responses.ToList(); }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            for (int i = 0; i < _responses.Count; i++)
            {
                if (_responses[i].Match(request))
                {
                    var resp = _responses[i].Response;
                    _responses.RemoveAt(i);
                    return Task.FromResult(resp);
                }
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class TimedHandler : HttpMessageHandler
    {
        private readonly TimeSpan _delay;
        public TimedHandler(TimeSpan delay) { _delay = delay; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(_delay, ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"answer":"slow"}""", Encoding.UTF8, "application/json"),
            };
        }
    }
}
