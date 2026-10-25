// Licensed under the MIT License. See LICENSE in the project root.

using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using ReadyCode.PocketReady;
using Xunit;

namespace ReadyCode.Tests;

/// <summary>Protocol/HTTP doubles: no Android networking or WPF dispatcher is simulated.</summary>
public sealed class PocketReadyControlTests
{
    private const string Instance = "12345678-1234-4234-8234-123456789abc";
    private const string Ticket = "87654321-1234-4234-9234-123456789abc";
    private static readonly string Token = new('T', 43);
    private static readonly Uri Base = new("http://phone.test:6401/proxy/");
    private static PocketReadyControl State => new() { InstanceId = Instance, Controller = "lan-console", Lease = 2, Generation = 0, Phase = "idle", Pending = false };
    private static HttpResponseMessage Json(object value, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    private static object Receipt(PocketReadyOperation request) => new { id = request.Id, token = 1, status = "applied", operation = request.Operation, error = (string?)null };

    [Fact]
    public async Task UploadPreservesSourceAndPollsApplicationReceiptWithoutChangingEnvelope()
    {
        const string source = "REM £ UTF-8\r\nSTART:PRINT \"Hello\"\nFUNCTION F(X)\nRETURN X\nEND FUNCTION";
        var operation = PocketReadyOperation.Create(State, "loadRun", source);
        int posts = 0;
        int polls = 0;
        using var http = new HttpClient(new Handler(async (request, cancellation) =>
        {
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal(Token, request.Headers.Authorization.Parameter);
            Assert.False(request.Headers.ExpectContinue ?? false);
            if (request.Method == HttpMethod.Post)
            {
                posts++;
                Assert.Equal("/proxy/pocketready/v1/requests", request.RequestUri!.AbsolutePath);
                var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellation));
                Assert.Equal(source, json.RootElement.GetProperty("text").GetString());
                Assert.Equal(operation.Id, json.RootElement.GetProperty("id").GetString());
                Assert.Equal(0, json.RootElement.GetProperty("generation").GetInt64());
                return Json(new { status = "queued", ticket = Ticket }, HttpStatusCode.Accepted);
            }
            Assert.Equal("/proxy/pocketready/v1/requests/" + Ticket, request.RequestUri!.AbsolutePath);
            return ++polls == 1 ? Json(new { status = "queued" }) : Json(new { status = "complete", receipt = Receipt(operation) });
        }));
        var client = new PocketReadyClient(http);
        var receipt = await client.ExecuteAsync(Base, Token, operation);
        Assert.Equal("applied", receipt.Status);
        Assert.Equal(operation.Id, receipt.Id);
        Assert.Equal(1, posts);
        Assert.Equal(2, polls);
        Assert.Null(http.DefaultRequestHeaders.Authorization);
        Assert.Equal(source, operation.Text);
    }

    [Fact]
    public async Task StateAndOutputParseBoundedLocatedDiagnostics()
    {
        using var http = new HttpClient(new Handler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/control"))
            {
                return Task.FromResult(Json(new { instanceId = Instance, controller = "lan-console", lease = 2, generation = 3, phase = "running", pending = false }));
            }
            if (request.Method == HttpMethod.Post)
            {
                return Task.FromResult(Json(new { status = "queued", ticket = Ticket }, HttpStatusCode.Accepted));
            }
            var location = new { sourceName = "PROGRAM", sourceId = "12:abc", line = 2, column = 4, label = "20", statement = "BAD", routine = "CALCULATE" };
            var diagnostic = new { location, frames = Array.Empty<object>() };
            var events = new[] { new { sequence = 10, runToken = 1, type = "error", text = "BAD EXPRESSION", diagnostic } };
            return Task.FromResult(Json(new { status = "complete", output = new { gap = true, next = 10, latest = 12, events } }));
        }));
        var client = new PocketReadyClient(http);
        var state = await client.GetControlAsync(Base, Token);
        Assert.Equal(3, state.Generation);
        var output = await client.ReadOutputAsync(Base, Token, state, 0);
        Assert.True(output.Gap);
        Assert.Equal(10, output.Next);
        Assert.Equal(12, output.Latest);
        Assert.Equal(2, Assert.Single(output.Events).Diagnostic!.Location!.Line);
    }

    [Theory]
    [InlineData("run", "unexpected")]
    [InlineData("stop", "unexpected")]
    [InlineData("input", "")]
    [InlineData("input", "12345678901234567")]
    [InlineData("assembly", "SYS")]
    public void InvalidOperationsFailBeforeTransport(string operation, string text) =>
        Assert.Throws<ArgumentException>(() => PocketReadyOperation.Create(State, operation, text));

    [Fact]
    public void SourceCapacityUsesUtf8AndSourceIdentityMatchesInterpreter()
    {
        Assert.Throws<ArgumentException>(() => PocketReadyOperation.Create(State, "load", new string('£', 327681)));
        Assert.Equal("0:811c9dc5", PocketReadyContract.SourceId(""));
        Assert.NotEqual(PocketReadyContract.SourceId("PRINT 1"), PocketReadyContract.SourceId("PRINT 2"));
        Assert.Equal("PRINT 1\r\n", PocketReadyOperation.Create(State, "load", "PRINT 1\r\n").Text);
    }

    [Theory]
    [InlineData("unknown", "DELIVERY_TIMEOUT")]
    [InlineData("rejected", "STALE_SESSION")]
    public async Task UncertainAndRejectedDeliveriesNeverAutomaticallyRetry(string status, string error)
    {
        int posts = 0;
        using var http = new HttpClient(new Handler((request, _) =>
        {
            if (request.Method == HttpMethod.Post) { posts++; return Task.FromResult(Json(new { status = "queued", ticket = Ticket }, HttpStatusCode.Accepted)); }
            return Task.FromResult(Json(new { status, error, message = "Refresh or query the original ID" }));
        }));
        var client = new PocketReadyClient(http);
        var operation = PocketReadyOperation.Create(State, "load", "PRINT 1");
        if (status == "unknown") await Assert.ThrowsAsync<PocketReadyOutcomeUnknownException>(() => client.ExecuteAsync(Base, Token, operation));
        else Assert.Equal(error, (await Assert.ThrowsAsync<PocketReadyRequestException>(() => client.ExecuteAsync(Base, Token, operation))).Code);
        Assert.Equal(1, posts);
    }

    [Fact]
    public async Task CancellationCoversPollingAndDoesNotRetryMutation()
    {
        using var cancellation = new CancellationTokenSource();
        int posts = 0;
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            if (request.Method == HttpMethod.Post) { posts++; return Json(new { status = "queued", ticket = Ticket }, HttpStatusCode.Accepted); }
            cancellation.Cancel();
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("Unreachable");
        }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new PocketReadyClient(http).ExecuteAsync(Base, Token,
            PocketReadyOperation.Create(State, "run"), cancellation.Token));
        Assert.Equal(1, posts);
    }

    [Theory]
    [InlineData("not-a-token")]
    [InlineData("Bearer something")]
    public async Task InvalidTokenNeverReachesTransport(string token)
    {
        using var http = new HttpClient(new Handler((_, _) => throw new InvalidOperationException("No request expected")));
        await Assert.ThrowsAsync<ArgumentException>(() => new PocketReadyClient(http).GetControlAsync(Base, token));
    }

    [Fact]
    public async Task ControlReplyLimitAndStrictUtf8AreEnforced()
    {
        foreach (byte[] body in new[] { new byte[262145], new byte[] { 0xc3, 0x28 } })
        {
            using var http = new HttpClient(new Handler((_, _) => {
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
                response.Content.Headers.ContentType = new("application/json");
                return Task.FromResult(response);
            }));
            await Assert.ThrowsAsync<PocketReadyProtocolException>(() => new PocketReadyClient(http).GetControlAsync(Base, Token));
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => reply(request, cancellationToken);
    }
}
