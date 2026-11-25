using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodeAssist.Models.Chat;

/// <summary>Builds the HTTP stack shared by every provider: header and body overlays, compression, timeouts and a concurrency cap.</summary>
public static class ProviderHttp
{
    /// <param name="innerHandler">Replaces the network handler; tests use it to capture requests.</param>
    public static HttpClient Create(ProviderConnection connection, HttpMessageHandler? innerHandler = null)
    {
        if (connection.HostedConnectors.Count > 0 && (!connection.HostedExecutionEnabled || connection.Wire != WireProtocol.Anthropic))
            throw new NotSupportedException("Hosted connector profiles require an explicitly admitted Messages API connection.");
        HttpMessageHandler handler = innerHandler ?? CreateNetworkHandler(connection);
        handler = new RequestShapingHandler(connection.Headers, ParseBodyOverlay(connection.Body), connection.Encoding, connection.HostedConnectors) { InnerHandler = handler };
        var gate = connection.MaxConcurrentRequests > 0 ? new SemaphoreSlim(connection.MaxConcurrentRequests) : null;
        handler = new LimitsHandler(connection.Limits, gate) { InnerHandler = handler };
        return new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private static SocketsHttpHandler CreateNetworkHandler(ProviderConnection connection)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(30),
            AutomaticDecompression = DecompressionMethods.All,
        };
        if (connection.Proxy is { Length: > 0 } proxy)
        {
            handler.Proxy = new WebProxy(proxy);
            handler.UseProxy = true;
        }
        return handler;
    }

    private static JsonObject ParseBodyOverlay(IReadOnlyDictionary<string, string> body)
    {
        var overlay = new JsonObject();
        foreach (var (name, json) in body)
        {
            try
            {
                overlay[name] = JsonNode.Parse(json);
            }
            catch (JsonException exception)
            {
                throw new ArgumentException($"Body field '{name}' is not valid JSON: {exception.Message}", nameof(body));
            }
        }
        return overlay;
    }

    internal static void Merge(JsonObject target, JsonObject overlay)
    {
        foreach (var (name, value) in overlay)
        {
            if (value is JsonObject incoming && target[name] is JsonObject existing) Merge(existing, incoming);
            else target[name] = value?.DeepClone();
        }
    }

    private sealed class RequestShapingHandler(
        IReadOnlyDictionary<string, string> headers, JsonObject bodyOverlay, BodyEncoding encoding, IReadOnlyList<HostedConnectorProfile> hosted) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            foreach (var (name, value) in headers)
            {
                request.Headers.Remove(name);
                request.Headers.TryAddWithoutValidation(name, value);
            }

            if (request.Content is not null && (bodyOverlay.Count > 0 || encoding == BodyEncoding.Gzip || hosted.Count > 0))
                request.Content = await RewriteBodyAsync(request.Content, cancellationToken);

            return await base.SendAsync(request, cancellationToken);
        }

        private async Task<HttpContent> RewriteBodyAsync(HttpContent original, CancellationToken cancellationToken)
        {
            var bytes = await original.ReadAsByteArrayAsync(cancellationToken);
            var mediaType = original.Headers.ContentType;
            var isJson = mediaType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true;

            if (isJson && (bodyOverlay.Count > 0 || hosted.Count > 0) && JsonNode.Parse(bytes) is JsonObject document)
            {
                Merge(document, bodyOverlay);
                if (hosted.Count > 0) HostedConnectorProfile.Apply(document, hosted);
                bytes = JsonSerializer.SerializeToUtf8Bytes(document);
            }

            var content = new ByteArrayContent(encoding == BodyEncoding.Gzip ? Compress(bytes) : bytes);
            content.Headers.ContentType = mediaType;
            if (encoding == BodyEncoding.Gzip) content.Headers.ContentEncoding.Add("gzip");
            return content;
        }

        private static byte[] Compress(byte[] bytes)
        {
            using var output = new MemoryStream();
            using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true)) gzip.Write(bytes);
            return output.ToArray();
        }
    }

    /// <summary>Applies the header timeout while waiting for the response and the chunk and total timeouts while reading it.</summary>
    private sealed class LimitsHandler(TransportLimits limits, SemaphoreSlim? gate) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (gate is not null) await gate.WaitAsync(cancellationToken);

            var total = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (limits.Total is { } totalLimit) total.CancelAfter(totalLimit);

            try
            {
                using var header = CancellationTokenSource.CreateLinkedTokenSource(total.Token);
                if (limits.Header is { } headerLimit) header.CancelAfter(headerLimit);

                HttpResponseMessage response;
                try
                {
                    response = await base.SendAsync(request, header.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException("The provider did not answer within the configured time.");
                }

                response.Content = new GuardedContent(response.Content, limits.Chunk, total, () => gate?.Release());
                return response;
            }
            catch
            {
                total.Dispose();
                gate?.Release();
                throw;
            }
        }
    }

    private sealed class GuardedContent : HttpContent
    {
        private readonly HttpContent _inner;
        private readonly TimeSpan? _chunk;
        private readonly CancellationTokenSource _total;
        private readonly Action _release;
        private int _released;

        public GuardedContent(HttpContent inner, TimeSpan? chunk, CancellationTokenSource total, Action release)
        {
            _inner = inner;
            _chunk = chunk;
            _total = total;
            _release = release;
            foreach (var header in inner.Headers) Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            await SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            await using var source = await CreateContentReadStreamAsync(cancellationToken);
            await source.CopyToAsync(stream, cancellationToken);
        }

        protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) =>
            new GuardedStream(await _inner.ReadAsStreamAsync(cancellationToken), _chunk, _total.Token, Finish);

        protected override Stream CreateContentReadStream(CancellationToken cancellationToken) =>
            new GuardedStream(_inner.ReadAsStream(cancellationToken), _chunk, _total.Token, Finish);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        private void Finish()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            _total.Dispose();
            _release();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
                Finish();
            }
            base.Dispose(disposing);
        }
    }

    private sealed class GuardedStream(Stream inner, TimeSpan? chunk, CancellationToken total, Action finish) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, total);
            if (chunk is { } idle) linked.CancelAfter(idle);
            try
            {
                return await inner.ReadAsync(buffer, linked.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("The provider stopped sending data for longer than the configured time.");
            }
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override int Read(Span<byte> buffer)
        {
            var temporary = new byte[buffer.Length];
            var read = ReadAsync(temporary.AsMemory()).AsTask().GetAwaiter().GetResult();
            temporary.AsSpan(0, read).CopyTo(buffer);
            return read;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                finish();
            }
            base.Dispose(disposing);
        }
    }
}
