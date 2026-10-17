// LoopbackAskRoutesTests.cs
//
// THE DESKTOP SERVICE EXE'S DOOR: the same questions-as-records over loopback HTTP, with
// the same token, the same "a retry is the same ask" and the same per-caller isolation
// as the phone's link. Run against a real listener on an auto-assigned port.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Assistant.Lifecycle;
using CircleAI.Assistant.Requests;
using CircleAI.Hosting;
using CircleAI.Hosting.Endpoints;
using Xunit;

namespace CircleAI.Tests;

public sealed class LoopbackAskRoutesTests
{
    private sealed class FakeFront : IAskFront
    {
        private readonly Dictionary<string, AskRecord> _asks = new();
        public event EventHandler<AskRecord>? Changed;

        public AskAdmission Submit(AskSubmission s)
        {
            var existing = _asks.Values.FirstOrDefault(a => a.CallerKey == s.CallerKey && a.ClientRequestId == s.ClientRequestId && s.ClientRequestId is not null);
            if (existing is not null) return AskAdmission.Of(existing, duplicate: true, 0);
            var rec = AskRecord.Accept(Guid.NewGuid().ToString("N"), s, DateTimeOffset.UtcNow) with
            {
                State = AskState.Succeeded, Text = "Nairobi.", Seq = _asks.Count + 1,
            };
            _asks[rec.Id] = rec;
            Changed?.Invoke(this, rec);
            return AskAdmission.Of(rec, duplicate: false, 0);
        }

        public AskRecord? Status(string callerKey, string askId) =>
            _asks.TryGetValue(askId, out var a) && a.CallerKey == callerKey ? a : null;

        public bool Cancel(string callerKey, string askId) => false;
        public IReadOnlyList<AskRecord> List(string callerKey, int limit, bool openOnly = false) =>
            _asks.Values.Where(a => a.CallerKey == callerKey).Take(limit).ToList();
        public int PositionOf(string askId) => -1;
        public Task<AskRecord?> WaitAsync(string callerKey, string askId, TimeSpan budget, CancellationToken ct) =>
            Task.FromResult(Status(callerKey, askId));
        public Task<AskRecord?> WaitForChangeAsync(string callerKey, string askId, long afterSeq, TimeSpan budget, CancellationToken ct) =>
            Task.FromResult(Status(callerKey, askId));
    }

    private static async Task<(HttpLoopbackEndpoint Endpoint, HttpClient Http)> StartAsync(FakeFront front)
    {
        var endpoint = new HttpLoopbackEndpoint(new AIOptions { LoopbackToken = "test-token" })
        {
            Asks = front,
            Readiness = new ReadinessTracker(),
        };
        await endpoint.StartAsync(new FakeAIService());
        var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{endpoint.BoundPort}/") };
        http.DefaultRequestHeaders.Add("X-Butler-Token", "test-token");
        return (endpoint, http);
    }

    private static StringContent Body(object payload) =>
        new(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

    [Fact]
    public async Task Without_the_token_nothing_is_served()
    {
        var (endpoint, _) = await StartAsync(new FakeFront());
        await using (endpoint)
        {
            using var bare = new HttpClient();
            var res = await bare.GetAsync($"http://127.0.0.1:{endpoint.BoundPort}/v1/status");
            Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        }
    }

    [Fact]
    public async Task A_question_is_accepted_and_a_retry_is_the_same_ask()
    {
        var (endpoint, http) = await StartAsync(new FakeFront());
        await using (endpoint)
        using (http)
        {
            var first = await http.PostAsync("v1/asks", Body(new { clientRequestId = "r1", message = "What is the capital of Kenya?" }));
            Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
            var a = JsonDocument.Parse(await first.Content.ReadAsStringAsync()).RootElement;
            var id = a.GetProperty("ask").GetProperty("id").GetString();

            var again = await http.PostAsync("v1/asks", Body(new { clientRequestId = "r1", message = "What is the capital of Kenya?" }));
            var b = JsonDocument.Parse(await again.Content.ReadAsStringAsync()).RootElement;
            Assert.True(b.GetProperty("duplicate").GetBoolean());
            Assert.Equal(id, b.GetProperty("ask").GetProperty("id").GetString());

            var status = JsonDocument.Parse(await http.GetStringAsync($"v1/asks/{id}")).RootElement;
            Assert.Equal("Succeeded", status.GetProperty("ask").GetProperty("state").GetString());
            Assert.Equal("Nairobi.", status.GetProperty("ask").GetProperty("text").GetString());
        }
    }

    [Fact]
    public async Task One_caller_cannot_read_another_callers_question()
    {
        var (endpoint, http) = await StartAsync(new FakeFront());
        await using (endpoint)
        using (http)
        {
            var res = await http.PostAsync("v1/asks", Body(new { message = "secret" }));
            var id = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement.GetProperty("ask").GetProperty("id").GetString();

            using var other = new HttpRequestMessage(HttpMethod.Get, $"v1/asks/{id}");
            other.Headers.Add("X-Circle-Caller", "someone-else");
            Assert.Equal(HttpStatusCode.NotFound, (await http.SendAsync(other)).StatusCode);
        }
    }

    [Fact]
    public async Task Status_reports_the_brain_and_says_unchanged_cheaply()
    {
        var (endpoint, http) = await StartAsync(new FakeFront());
        await using (endpoint)
        using (http)
        {
            var now = JsonDocument.Parse(await http.GetStringAsync("v1/status")).RootElement;
            var seq = now.GetProperty("seq").GetInt64();
            Assert.Equal("Unknown", now.GetProperty("brain").GetProperty("phase").GetString());

            var again = JsonDocument.Parse(await http.GetStringAsync($"v1/status?after={seq}")).RootElement;
            Assert.True(again.GetProperty("unchanged").GetBoolean());
        }
    }
}
