using System.Net;
using System.Net.Sockets;

namespace MediaStudio.Sources;

public sealed class PublicDocumentFetcher
{
    private readonly HttpMessageHandler? fixtureHandler;
    public PublicDocumentFetcher() { }
    internal PublicDocumentFetcher(HttpMessageHandler fixtureHandler) => this.fixtureHandler = fixtureHandler;
    public static Uri ValidateLink(string link)
    {
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0
            || uri.HostNameType == UriHostNameType.Unknown
            || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || uri.HostNameType == UriHostNameType.Dns && !uri.Host.Contains('.')
            || uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
            throw new SourceException("source.link", "Use a public HTTPS PDF/text/Markdown link without credentials, query tokens, fragments or a custom port.");
        if (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address) && !IsPublic(address))
            throw new SourceException("source.private-address", "Private, local or reserved network sources cannot be read.");
        return uri;
    }
    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) return IsPublic(address.MapToIPv4());
        if (IPAddress.IsLoopback(address)) return false;
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
            return bytes[0] is not (0 or 10 or 127) && bytes[0] < 224
                && !(bytes[0] == 100 && bytes[1] is >= 64 and <= 127)
                && !(bytes[0] == 169 && bytes[1] == 254)
                && !(bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
                && !(bytes[0] == 192 && (bytes[1] == 168 || bytes[1] == 0 && bytes[2] is 0 or 2
                    || bytes[1] == 88 && bytes[2] == 99))
                && !(bytes[0] == 198 && (bytes[1] is 18 or 19 || bytes[1] == 51 && bytes[2] == 100))
                && !(bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113);
        return address.AddressFamily == AddressFamily.InterNetworkV6 && (bytes[0] & 0xe0) == 0x20
            && !(bytes[0] == 0x20 && bytes[1] == 0x02)
            && !(bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0 && bytes[3] == 0)
            && !(bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0d && bytes[3] == 0xb8);
    }
    private static async ValueTask<Stream> Connect(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var addresses = ValidateAddresses(await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct));
        // Connect to these validated addresses, not another DNS resolution after validation.
        Exception? last = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try { await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), ct); return new NetworkStream(socket, ownsSocket: true); }
            catch (SocketException error) { socket.Dispose(); last = error; }
            catch { socket.Dispose(); throw; }
        }
        throw new SourceException("source.connection", "The public document server could not be reached: " + last?.Message);
    }
    internal static IPAddress[] ValidateAddresses(IPAddress[] addresses)
    {
        if (addresses.Length == 0 || addresses.Any(a => !IsPublic(a)))
            throw new SourceException("source.private-address", "The document host resolves to a private, local or reserved address.");
        return addresses;
    }
    public async Task<DocumentSource> ReadAsync(DocumentSourceStore store, string link, CancellationToken ct = default)
    {
        var uri = ValidateLink(link);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        using var handler = fixtureHandler ?? new SocketsHttpHandler {
            AllowAutoRedirect = false, UseCookies = false, UseProxy = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectCallback = Connect, ConnectTimeout = TimeSpan.FromSeconds(10), MaxResponseHeadersLength = 16
        };
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        for (var redirects = 0; ; redirects++)
        {
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (response.StatusCode is HttpStatusCode.Moved or HttpStatusCode.Redirect or HttpStatusCode.RedirectMethod
                or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                if (redirects >= 3 || response.Headers.Location is null)
                    throw new SourceException("source.redirect", "The document link redirects too many times or has no destination.");
                uri = ValidateLink(new Uri(uri, response.Headers.Location).AbsoluteUri);
                continue;
            }
            if (!response.IsSuccessStatusCode) throw new SourceException("source.http", $"The document server returned HTTP{(int)response.StatusCode}.");
            if (response.Content.Headers.ContentLength is > DocumentSourceStore.MaximumBytes)
                throw new SourceException("source.bytes", "This linked document exceeds20MB.");
            if (response.Content.Headers.ContentEncoding.Count != 0)
                throw new SourceException("source.encoding", "Encoded/compressed HTTP documents are not supported by this bounded demo.");
            var name = Path.GetFileName(Uri.UnescapeDataString(uri.AbsolutePath));
            if (string.IsNullOrWhiteSpace(name)) throw new SourceException("source.link-type", "Use a direct PDF/text/Markdown document link, not a webpage.");
            await using var content = await response.Content.ReadAsStreamAsync(deadline.Token);
            return await store.AddAsync(content, name, response.Content.Headers.ContentType?.MediaType, deadline.Token,
                publicOrigin: uri.GetLeftPart(UriPartial.Path));
        }
    }
}
