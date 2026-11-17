using System.IO;
using System.Net.Http.Headers;
using System.Windows;
using System.Windows.Controls;
using Microsoft.AspNetCore.Components.WebView.Wpf;
using Microsoft.Web.WebView2.Core;
using VideoProduction;

namespace MediaStudio.App;

public sealed class VerifiedResultWebView : BlazorWebView
{
    public VerifiedResultWebView(FileProductionSession? production) : this(() => production) { }

    public VerifiedResultWebView(Func<FileProductionSession?> currentProduction) : this(uri =>
    {
        var production = currentProduction();
        if (production is null) return (null, "application/octet-stream");
        var stream = production.OpenPreview(uri, out var type);
        return (stream, type);
    })
    { }

    public VerifiedResultWebView(Func<Uri, (Stream? Stream, string ContentType)> openPreview)
    {
        // The pinned RC2 host resumes async cleanup off-dispatcher. Marshal native disposal via a WPF template.
        UseCompositionControl = false;
        Template = new ControlTemplate(typeof(BlazorWebView)) {
            VisualTree = new FrameworkElementFactory(typeof(DispatcherWebView)) { Name = "WebView" }
        };
        BlazorWebViewInitialized += (_, initialized) =>
        {
            var core = initialized.WebView.CoreWebView2;
            var registered = false;
            // The framework installs its static handler after BlazorWebViewInitialized.
            core.DOMContentLoaded += (_, _) =>
            {
                if (registered) return;
                registered = true;
                core.AddWebResourceRequestedFilter("https://0.0.0.1/hybrid-result/*", CoreWebView2WebResourceContext.All);
                core.WebResourceRequested += (_, request) =>
                {
                    Respond(core, openPreview, request);
                };
            };
        };
    }

    private static void Respond(CoreWebView2 core, Func<Uri, (Stream? Stream, string ContentType)> openPreview,
        CoreWebView2WebResourceRequestedEventArgs request)
    {
        Stream? source = null;
        try
        {
            (source, var contentType) = openPreview(new Uri(request.Request.Uri));
            if (source is null) return;
            var total = source.Length;
            var header = request.Request.Headers.Contains("Range") ? request.Request.Headers.GetHeader("Range") : null;
            if (!TryRange(header, total, out var start, out var count))
            {
                source.Dispose();
                request.Response = core.Environment.CreateWebResourceResponse(null, 416, "Range Not Satisfiable",
                    $"Content-Range: bytes */{total}\r\nAccept-Ranges: bytes");
                return;
            }
            if (request.Request.Method is not ("GET" or "HEAD"))
            {
                source.Dispose();
                request.Response = core.Environment.CreateWebResourceResponse(null, 405, "Method Not Allowed", "Allow: GET, HEAD");
                return;
            }
            var ranged = header is not null;
            var headers = $"Content-Type: {contentType}\r\nContent-Length: {count}\r\nAccept-Ranges: bytes\r\nCache-Control: no-store";
            if (ranged) headers += $"\r\nContent-Range: bytes {start}-{start + count - 1}/{total}";
            var body = request.Request.Method == "HEAD" ? null : new WindowStream(source, start, count);
            if (body is null) source.Dispose();
            request.Response = core.Environment.CreateWebResourceResponse(body, ranged ? 206 : 200,
                ranged ? "Partial Content" : "OK", headers);
        }
        catch (Exception error) when (error is IOException or VideoException or UnauthorizedAccessException)
        {
            source?.Dispose();
            Console.Error.WriteLine("Verified artifact preview failed: " + error.Message);
            request.Response = core.Environment.CreateWebResourceResponse(null, 500, "Artifact verification failed", "");
        }
    }

    internal static bool TryRange(string? header, long total, out long start, out long count)
    {
        start = 0;
        count = total;
        if (total <= 0) return false;
        if (header is null) return true;
        if (!RangeHeaderValue.TryParse(header, out var range)
            || !range.Unit.Equals("bytes", StringComparison.OrdinalIgnoreCase) || range.Ranges.Count != 1)
            return false;
        var item = range.Ranges.Single();
        if (item.From is null)
        {
            if (item.To is not > 0) return false;
            count = Math.Min(item.To.Value, total);
            start = total - count;
        }
        else
        {
            start = item.From.Value;
            var end = Math.Min(item.To ?? total - 1, total - 1);
            if (start >= total || end < start) return false;
            count = end - start + 1;
        }
        return true;
    }

    internal sealed class WindowStream(Stream source, long offset, long count) : Stream
    {
        private long position;
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => count;
        public override long Position { get => position; set => Seek(value, SeekOrigin.Begin); }
        public override int Read(byte[] buffer, int start, int length) => Read(buffer.AsSpan(start, length));
        public override int Read(Span<byte> buffer)
        {
            source.Position = offset + position;
            var read = source.Read(buffer[..(int)Math.Min(buffer.Length, count - position)]);
            position += read;
            return read;
        }
        public override long Seek(long value, SeekOrigin origin)
        {
            var next = checked(origin switch {
                SeekOrigin.Begin => value,
                SeekOrigin.Current => position + value,
                SeekOrigin.End => count + value,
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            });
            if (next < 0 || next > count) throw new IOException("Seek outside the verified response window.");
            return position = next;
        }
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) source.Dispose(); base.Dispose(disposing); }
    }

    public sealed class DispatcherWebView : Microsoft.Web.WebView2.Wpf.WebView2
    {
        protected override void Dispose(bool disposing)
        {
            if (disposing && !Dispatcher.CheckAccess())
                Dispatcher.Invoke(() => base.Dispose(disposing));
            else base.Dispose(disposing);
        }
    }
}
