using CodeAssist.Configuration;
using OpenTelemetry.Exporter;

namespace CodeAssist.Server.Telemetry;

internal sealed class TelemetryExportConfiguration
{
    public bool Enabled { get; }
    public bool Traces { get; }
    public bool Metrics { get; }
    public bool Logs { get; }
    public double TraceSampleRatio { get; }
    public int TimeoutMilliseconds { get; }
    public int MetricIntervalMilliseconds { get; }
    private readonly Uri? _endpoint;
    private readonly OtlpExportProtocol _protocol;
    private readonly string? _headers;

    public TelemetryExportConfiguration(TelemetrySettings settings, IReadOnlyDictionary<string, string> environment)
    {
        Enabled = settings.Enabled && !string.Equals(environment.GetValueOrDefault("OTEL_SDK_DISABLED"), "true", StringComparison.OrdinalIgnoreCase);
        if (!Enabled) return;
        if (!Enum.IsDefined(settings.Protocol) || !double.IsFinite(settings.TraceSampleRatio) || settings.TraceSampleRatio is < 0 or > 1 ||
            settings.ExportTimeoutSeconds is < 1 or > 300 || settings.MetricIntervalSeconds is < 1 or > 3600 || settings.Headers.Count > 16)
            throw new ArgumentException("Invalid telemetry limits.");
        if (!Uri.TryCreate(settings.Endpoint ?? (settings.Protocol == TelemetryProtocol.Grpc ? "http://127.0.0.1:4317" : "http://127.0.0.1:4318"), UriKind.Absolute, out var endpoint) ||
            endpoint.AbsoluteUri.Length > 2048 || endpoint.Scheme is not ("http" or "https") || endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 ||
            endpoint.Fragment.Length != 0 || endpoint.Scheme == "http" && !endpoint.IsLoopback)
            throw new ArgumentException("Telemetry requires HTTPS or loopback HTTP without URL credentials, queries or fragments.");
        _endpoint = endpoint;
        _protocol = settings.Protocol == TelemetryProtocol.Grpc ? OtlpExportProtocol.Grpc : OtlpExportProtocol.HttpProtobuf;
        _headers = string.Join(",", settings.Headers.Select(pair =>
        {
            if (pair.Key.Length is < 1 or > 64 || pair.Key.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-') ||
                !EnvironmentReference.TryParse(pair.Value, out var variable)) throw new ArgumentException("Telemetry headers require bounded names and complete environment references.");
            var value = environment.GetValueOrDefault(variable);
            if (string.IsNullOrWhiteSpace(value) || value.Length > 4096 || value.Contains('\r') || value.Contains('\n') || value.Contains("{env:", StringComparison.Ordinal))
                throw new ArgumentException("Telemetry header reference is unavailable or invalid.");
            return $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(value)}";
        }));
        Traces = settings.Traces;
        Metrics = settings.Metrics;
        Logs = settings.Logs;
        TraceSampleRatio = settings.TraceSampleRatio;
        TimeoutMilliseconds = checked(settings.ExportTimeoutSeconds * 1000);
        MetricIntervalMilliseconds = checked(settings.MetricIntervalSeconds * 1000);
    }

    public void Configure(OtlpExporterOptions options, string signal)
    {
        options.Protocol = _protocol;
        options.Endpoint = _protocol == OtlpExportProtocol.Grpc ? _endpoint! : new Uri(_endpoint!.AbsoluteUri.TrimEnd('/') + "/v1/" + signal);
        options.Headers = _headers!;
        options.TimeoutMilliseconds = TimeoutMilliseconds;
        options.HttpClientFactory = () => new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
    }
}