// Licensed under the MIT License. See LICENSE in the project root.

using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using ReadyCode.PocketReady;
using Xunit;

namespace ReadyCode.Tests;

/// <summary>
/// Contract tests use the same current discovery fixture as Pocket READY.'s JS
/// handler. Fake HTTP proves client behavior, not Android LAN or WPF integration.
/// </summary>
public sealed class PocketReadyClientTests
{
    private static string Fixture => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "pocket-ready-discovery.json"));

    [Theory]
    [InlineData("http://192.168.1.20:6401", "http://192.168.1.20:6401/pocketready/v1/info")]
    [InlineData("https://example.test/pocket", "https://example.test/pocket/pocketready/v1/info")]
    [InlineData("https://example.test/pocket/", "https://example.test/pocket/pocketready/v1/info")]
    public async Task GetInfo_UsesPocketReadyRouteAndPreservesProxyBasePath(string baseUrl, string expectedUrl)
    {
        string? requestedUrl = null;
        using var http = new HttpClient(new ReplyHandler((request, _) =>
        {
            requestedUrl = request.RequestUri!.AbsoluteUri;
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Contains(request.Headers.Accept, value => value.MediaType == "application/json");
            return Task.FromResult(JsonResponse(Fixture));
        }));
        var discovery = await new PocketReadyClient(http).GetInfoAsync(new Uri(baseUrl));

        Assert.Equal(expectedUrl, requestedUrl);
        Assert.Equal("Pocket READY.", discovery.Product.Name);
        Assert.Equal("pocket-ready-basic", discovery.Language.Id);
        Assert.Equal("UTF-8", discovery.Language.SourceEncoding);
        Assert.Equal(655360, discovery.Language.SourceCapacityBytes);
        Assert.Equal("optional", discovery.Language.LineLabels);
        Assert.Equal(64, discovery.Device.SpriteSlots);
        Assert.Empty(discovery.Capabilities.Transports);
        Assert.Empty(discovery.Capabilities.ControlOperations);
        Assert.False(discovery.Capabilities.Assembly);
        Assert.False(discovery.Capabilities.CpuDebug);
        Assert.False(discovery.Capabilities.Output);
    }

    [Theory]
    [InlineData("file:///tmp/phone")]
    [InlineData("ftp://example.test")]
    [InlineData("http://example.test?run=1")]
    [InlineData("http://example.test/#fragment")]
    [InlineData("http://user:password@example.test")]
    public async Task GetInfo_InvalidBaseAddressDoesNotSendARequest(string address)
    {
        using var http = new HttpClient(new ReplyHandler((_, _) => throw new InvalidOperationException("Must not send invalid addresses")));
        await Assert.ThrowsAsync<ArgumentException>(() => new PocketReadyClient(http).GetInfoAsync(new Uri(address)));
    }

    [Fact]
    public void Parse_AllowsFutureMinorVersionsAndAdditiveFields()
    {
        var json = JsonNode.Parse(Fixture)!;
        json["protocol"]!["minor"] = 7;
        json["futureProperty"] = "ignored";
        json["capabilities"]!["futureCapability"] = true;
        Assert.Equal(7, PocketReadyDiscovery.Parse(json.ToJsonString()).Protocol.Minor);
    }

    [Theory]
    [InlineData("protocol", "name", "c64-ultimate")]
    [InlineData("product", "name", "Another calculator")]
    [InlineData("product", "version", "0.6.43\n")]
    [InlineData("language", "sourceEncoding", "PETSCII")]
    [InlineData("language", "id", "commodore-basic-v2")]
    [InlineData("session", "phase", "not-a-state")]
    public void Parse_RejectsWrongIdentityAndSourceFormat(string section, string property, string value)
    {
        var json = JsonNode.Parse(Fixture)!;
        json[section]![property] = value;
        Assert.Throws<PocketReadyProtocolException>(() => PocketReadyDiscovery.Parse(json.ToJsonString()));
    }

    [Theory]
    [InlineData("protocol", "major", 2)]
    [InlineData("protocol", "minor", -1)]
    [InlineData("language", "sourceCapacityBytes", 0)]
    [InlineData("device", "abi", 0)]
    [InlineData("session", "generation", -1)]
    public void Parse_RejectsInvalidNumericContractFields(string section, string property, int value)
    {
        var json = JsonNode.Parse(Fixture)!;
        json[section]![property] = value;
        Assert.Throws<PocketReadyProtocolException>(() => PocketReadyDiscovery.Parse(json.ToJsonString()));
    }

    [Theory]
    [InlineData("not JSON")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"protocol\":null}")]
    public void Parse_RejectsMalformedOrMissingDeclarations(string json)
    {
        Assert.Throws<PocketReadyProtocolException>(() => PocketReadyDiscovery.Parse(json));
    }

    [Fact]
    public void Parse_RequiresExplicitCapabilityFlagsAndValidInstanceIdentity()
    {
        var json = JsonNode.Parse(Fixture)!;
        json["capabilities"]!.AsObject().Remove("assembly");
        Assert.Throws<PocketReadyProtocolException>(() => PocketReadyDiscovery.Parse(json.ToJsonString()));
        json = JsonNode.Parse(Fixture)!;
        json["instanceId"] = "00000000-0000-0000-0000-000000000000";
        Assert.Throws<PocketReadyProtocolException>(() => PocketReadyDiscovery.Parse(json.ToJsonString()));
    }

    [Theory]
    [InlineData("text/html", "<html>not discovery</html>")]
    [InlineData("application/json; charset=utf-16", "{}")]
    public async Task GetInfo_RejectsWrongMediaTypeOrEncoding(string mediaType, string body)
    {
        using var http = new HttpClient(new ReplyHandler((_, _) =>
        {
            var response = JsonResponse(body);
            response.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(mediaType);
            return Task.FromResult(response);
        }));
        await Assert.ThrowsAsync<PocketReadyProtocolException>(() => new PocketReadyClient(http).GetInfoAsync(new Uri("http://phone.test")));
    }

    [Fact]
    public async Task GetInfo_RejectsNonSuccessWithoutParsingItsBody()
    {
        using var http = new HttpClient(new ReplyHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => new PocketReadyClient(http).GetInfoAsync(new Uri("http://phone.test")));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, error.StatusCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GetInfo_BoundsDeclaredAndChunkedBodiesAndDisposesResources(bool declaredLength)
    {
        var body = new NonSeekableBody(new byte[65537]);
        using var http = new HttpClient(new ReplyHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) };
            response.Content.Headers.ContentType = new("application/json");
            if (declaredLength)
            {
                response.Content.Headers.ContentLength = 65537;
            }
            return Task.FromResult(response);
        }));
        await Assert.ThrowsAsync<PocketReadyProtocolException>(() => new PocketReadyClient(http).GetInfoAsync(new Uri("http://phone.test")));
        Assert.True(body.Disposed);
        if (declaredLength)
        {
            Assert.Equal(0, body.ReadCalls);
        }
        else
        {
            Assert.True(body.ReadCalls > 0);
        }
    }

    [Fact]
    public async Task GetInfo_RejectsInvalidUtf8InsteadOfReplacingBytes()
    {
        using var http = new HttpClient(new ReplyHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([0xc3, 0x28]) };
            response.Content.Headers.ContentType = new("application/json");
            return Task.FromResult(response);
        }));
        await Assert.ThrowsAsync<PocketReadyProtocolException>(() => new PocketReadyClient(http).GetInfoAsync(new Uri("http://phone.test")));
    }

    [Fact]
    public async Task GetInfo_CallerCanCancelWhileWaitingForHeaders()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        using var http = new HttpClient(new ReplyHandler(async (_, token) =>
        {
            entered.SetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return JsonResponse(Fixture);
        }));
        var operation = new PocketReadyClient(http).GetInfoAsync(new Uri("http://phone.test"), cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
    }

    [Fact]
    public async Task GetInfo_CallerCanCancelDuringBodyReadAndStreamIsDisposed()
    {
        var body = new BlockingBody();
        using var cancellation = new CancellationTokenSource();
        using var http = new HttpClient(new ReplyHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) };
            response.Content.Headers.ContentType = new("application/json");
            return Task.FromResult(response);
        }));
        var operation = new PocketReadyClient(http).GetInfoAsync(new Uri("http://phone.test"), cancellation.Token);
        await body.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.True(body.Disposed);
    }

    [Fact]
    public async Task GetInfo_ConfiguredDeadlineCancelsAnUnresponsivePeer()
    {
        using var http = new HttpClient(new ReplyHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return JsonResponse(Fixture);
        }));
        var client = new PocketReadyClient(http, TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetInfoAsync(new Uri("http://phone.test")).WaitAsync(TimeSpan.FromSeconds(5)));
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private sealed class ReplyHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _reply;

        public ReplyHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> reply)
        {
            _reply = reply;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => _reply(request, cancellationToken);
    }

    private class NonSeekableBody : Stream
    {
        private readonly MemoryStream _inner;
        public bool Disposed { get; private set; }
        public int ReadCalls { get; private set; }

        public NonSeekableBody(byte[] bytes)
        {
            _inner = new MemoryStream(bytes);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadCalls++;
            return _inner.ReadAsync(buffer, cancellationToken);
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Disposed = true;
                _inner.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    private sealed class BlockingBody : NonSeekableBody
    {
        public TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public BlockingBody() : base([]) { }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }
}
