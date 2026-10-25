// Licensed under the MIT License. See LICENSE in the project root.

using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;

namespace ReadyCode.PocketReady;

/// <summary>
/// Read-only discovery client for the Pocket READY. target. The caller owns the
/// injected HttpClient; no static connection or WPF/dispatcher state is retained.
/// Upload, execution, memory, assembly and C64 token conversion are not provided.
/// </summary>
public sealed partial class PocketReadyClient
{
    private const int MaxDiscoveryBytes = 64 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly HttpClient _httpClient;
    private readonly TimeSpan _requestTimeout;

    public PocketReadyClient(HttpClient httpClient, TimeSpan? requestTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _httpClient = httpClient;
        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(15);
        if (_requestTimeout <= TimeSpan.Zero || _requestTimeout > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(nameof(requestTimeout), "Discovery timeout must be positive and at most five minutes.");
        }
    }

    /// <summary>
    /// GETs /pocketready/v1/info, preserving an optional reverse-proxy base path. Cancellation
    /// covers headers and body; both declared and streamed response sizes are bounded.
    /// A returned descriptor is not a grant of control or evidence of deployment support.
    /// </summary>
    public async Task<PocketReadyDiscovery> GetInfoAsync(Uri baseUri, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        if (!baseUri.IsAbsoluteUri || (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps) ||
            !string.IsNullOrEmpty(baseUri.Query) || !string.IsNullOrEmpty(baseUri.Fragment) || !string.IsNullOrEmpty(baseUri.UserInfo))
        {
            throw new ArgumentException("Use an absolute HTTP(S) base URL without query, fragment or embedded credentials.", nameof(baseUri));
        }

        var directory = baseUri.AbsoluteUri.EndsWith('/') ? baseUri : new Uri(baseUri.AbsoluteUri + "/");
        var endpoint = new Uri(directory, "pocketready/v1/info");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_requestTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var contentType = response.Content.Headers.ContentType;
        var charset = contentType?.CharSet;
        if (!string.Equals(contentType?.MediaType, "application/json", StringComparison.OrdinalIgnoreCase) ||
            (charset is not null && !string.Equals(charset.Trim('"'), "utf-8", StringComparison.OrdinalIgnoreCase)))
        {
            throw new PocketReadyProtocolException("Discovery must be application/json encoded as UTF-8.");
        }
        if (response.Content.Headers.ContentLength > MaxDiscoveryBytes)
        {
            throw new PocketReadyProtocolException("Discovery exceeds the 64 KiB response limit.");
        }

        using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var bytes = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            int count = await stream.ReadAsync(buffer.AsMemory(), timeout.Token).ConfigureAwait(false);
            if (count == 0)
            {
                break;
            }
            if (bytes.Length + count > MaxDiscoveryBytes)
            {
                throw new PocketReadyProtocolException("Discovery exceeds the 64 KiB response limit.");
            }
            bytes.Write(buffer, 0, count);
        }

        string json;
        try
        {
            json = StrictUtf8.GetString(bytes.GetBuffer(), 0, (int)bytes.Length);
        }
        catch (DecoderFallbackException exception)
        {
            throw new PocketReadyProtocolException("Discovery contains invalid UTF-8 bytes.", exception);
        }
        return PocketReadyDiscovery.Parse(json);
    }
}
