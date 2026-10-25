// Licensed under the MIT License. See LICENSE in the project root.

using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ReadyCode.PocketReady;

public sealed partial class PocketReadyClient
{
    /// <summary>Authenticated advisory state; this does not acquire a phone-side control grant.</summary>
    public async Task<PocketReadyControl> GetControlAsync(Uri baseUri, string token, CancellationToken cancellationToken = default)
    {
        var state = PocketReadyContract.Parse<PocketReadyControl>(await ControlJsonAsync(baseUri, "control", token, null, cancellationToken).ConfigureAwait(false));
        state.Validate();
        return state;
    }

    /// <summary>
    /// One immutable operation, one delivery ticket, bounded polling until application receipt.
    /// Never automatically retries a mutation. Cancellation/timeout may follow an applied action;
    /// preserve the envelope and deliberately retry/query its ID rather than generate a new ID.
    /// </summary>
    public async Task<PocketReadyReceipt> ExecuteAsync(Uri baseUri, string token, PocketReadyOperation operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        operation.Validate();
        var reply = await DeliverAsync(baseUri, token, operation, cancellationToken).ConfigureAwait(false);
        var receipt = reply.Receipt ?? throw new PocketReadyProtocolException("Operation has no application receipt.");
        receipt.Validate();
        if (receipt.Id != operation.Id || receipt.Operation != operation.Operation)
            throw new PocketReadyProtocolException("Receipt belongs to another operation.");
        return receipt;
    }

    public async Task<PocketReadyReceipt?> GetReceiptAsync(Uri baseUri, string token, PocketReadyControl state, string id, CancellationToken cancellationToken = default)
    {
        state.Validate();
        if (!PocketReadyContract.IsId(id)) throw new ArgumentException("Invalid operation ID.", nameof(id));
        var reply = await DeliverAsync(baseUri, token, new { kind = "receipt", state.InstanceId, state.Controller, state.Lease, id }, cancellationToken).ConfigureAwait(false);
        reply.Receipt?.Validate();
        if (reply.Receipt is not null && reply.Receipt.Id != id) throw new PocketReadyProtocolException("Receipt belongs to another operation.");
        return reply.Receipt;
    }

    public async Task<PocketReadyOutput> ReadOutputAsync(Uri baseUri, string token, PocketReadyControl state, long after, CancellationToken cancellationToken = default)
    {
        state.Validate();
        if (!PocketReadyContract.IsInteger(after)) throw new ArgumentOutOfRangeException(nameof(after));
        var reply = await DeliverAsync(baseUri, token, new { kind = "output", state.InstanceId, state.Controller, state.Lease, after }, cancellationToken).ConfigureAwait(false);
        var output = reply.Output ?? throw new PocketReadyProtocolException("Missing output page.");
        output.Validate(after);
        return output;
    }

    private async Task<DeliveryReply> DeliverAsync(Uri baseUri, string token, object body, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_requestTimeout);
        var queued = PocketReadyContract.Parse<QueuedReply>(await ControlJsonAsync(baseUri, "requests", token, body, deadline.Token).ConfigureAwait(false));
        if (queued.Status != "queued" || !PocketReadyContract.IsInstance(queued.Ticket))
            throw new PocketReadyProtocolException("Malformed native delivery ticket.");
        while (true)
        {
            var reply = PocketReadyContract.Parse<DeliveryReply>(await ControlJsonAsync(baseUri, "requests/" + queued.Ticket, token, null, deadline.Token).ConfigureAwait(false));
            switch (reply.Status)
            {
                case "queued":
                    await Task.Delay(100, deadline.Token).ConfigureAwait(false);
                    break;
                case "complete":
                    return reply;
                case "rejected":
                    throw new PocketReadyRequestException(reply.Error ?? "INVALID_REQUEST", reply.Message ?? "Request rejected.");
                case "unknown":
                    throw new PocketReadyOutcomeUnknownException("Delivery expired; the action may have applied. Query/retry the same operation ID.");
                default:
                    throw new PocketReadyProtocolException("Unsupported delivery status.");
            }
        }
    }

    // The caller must configure its injected HttpClient with AllowAutoRedirect=false.
    // The WPF connection window does so; bearer tokens never go in DefaultRequestHeaders.
    private async Task<string> ControlJsonAsync(Uri baseUri, string route, string token, object? body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        if (token is null || !Regex.IsMatch(token, @"\A[A-Za-z0-9_-]{43}\z"))
            throw new ArgumentException("Use the temporary token shown on the phone.", nameof(token));
        if (!baseUri.IsAbsoluteUri || baseUri.Scheme is not ("http" or "https") ||
            baseUri.Query.Length != 0 || baseUri.Fragment.Length != 0 || baseUri.UserInfo.Length != 0)
            throw new ArgumentException("Use an absolute HTTP(S) base URL without credentials, query or fragment.", nameof(baseUri));
        var directory = baseUri.AbsoluteUri.EndsWith('/') ? baseUri : new Uri(baseUri.AbsoluteUri + "/");
        using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post,
            new Uri(directory, "pocketready/v1/" + route));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.ExpectContinue = false;
        if (body is not null)
        {
            byte[] bytes = PocketReadyContract.Utf8.GetBytes(JsonSerializer.Serialize(body, PocketReadyContract.Json));
            if (bytes.Length > 4 * 1024 * 1024) throw new ArgumentException("JSON request exceeds 4 MiB.");
            request.Content = new ByteArrayContent(bytes);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_requestTimeout);
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.StatusCode != (body is null ? HttpStatusCode.OK : HttpStatusCode.Accepted))
            throw new PocketReadyProtocolException("Unexpected control HTTP status.");
        var type = response.Content.Headers.ContentType;
        if (!string.Equals(type?.MediaType, "application/json", StringComparison.OrdinalIgnoreCase) ||
            (type.CharSet is not null && !string.Equals(type.CharSet.Trim('"'), "utf-8", StringComparison.OrdinalIgnoreCase)))
            throw new PocketReadyProtocolException("Control replies must be UTF-8 JSON.");
        const int limit = 256 * 1024;
        if (response.Content.Headers.ContentLength > limit) throw new PocketReadyProtocolException("Control reply exceeds 256 KiB.");
        using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
        using var data = new MemoryStream();
        byte[] buffer = new byte[4096];
        while (true)
        {
            int count = await stream.ReadAsync(buffer.AsMemory(), deadline.Token).ConfigureAwait(false);
            if (count == 0) break;
            if (data.Length + count > limit) throw new PocketReadyProtocolException("Control reply exceeds 256 KiB.");
            data.Write(buffer, 0, count);
        }
        try
        {
            return PocketReadyContract.Utf8.GetString(data.GetBuffer(), 0, (int)data.Length);
        }
        catch (DecoderFallbackException error)
        {
            throw new PocketReadyProtocolException("Control reply contains invalid UTF-8.", error);
        }
    }

    private sealed record QueuedReply
    {
        public required string Status { get; init; }
        public required string Ticket { get; init; }
    }

    private sealed record DeliveryReply
    {
        public required string Status { get; init; }
        public PocketReadyReceipt? Receipt { get; init; }
        public PocketReadyOutput? Output { get; init; }
        public string? Error { get; init; }
        public string? Message { get; init; }
    }
}
