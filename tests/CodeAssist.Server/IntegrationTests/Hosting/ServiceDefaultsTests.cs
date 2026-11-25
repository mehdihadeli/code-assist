using System.Diagnostics;
using CodeAssist.Testing;
using CodeAssist.Tools.Diagnostics;
using CodeAssist.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Shouldly;

namespace CodeAssist.Server.IntegrationTests.Hosting;

public sealed class ServiceDefaultsTests
{
    [Theory]
    [InlineData(true, null, true, true)]
    [InlineData(true, "false", true, true)]
    [InlineData(true, "TRUE", true, false)]
    [InlineData(false, null, true, false)]
    [InlineData(true, null, false, false)]
    public async Task Should_RespectSamplingControls_When_HostStarts(bool enabled, string? sdkDisabled, bool traces, bool expectedRecorded)
    {
        using var files = new SettingsSandbox();
        var environment = Environment(enabled);
        environment["Telemetry__Traces"] = traces.ToString();
        if (sdkDisabled is not null) environment["OTEL_SDK_DISABLED"] = sdkDisabled;
        using var host = BuildHost(files, environment);

        await host.StartAsync(TestContext.Current.CancellationToken);
        host.Services.GetRequiredService<TracerProvider>().ShouldNotBeNull();
        host.Services.GetRequiredService<MeterProvider>().ShouldNotBeNull();
        {
            using var activity = RuntimeTelemetry.Activities.StartActivity("template-test", ActivityKind.Internal);
            (activity?.Recorded ?? false).ShouldBe(expectedRecorded);
        }
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("grpc", OtlpExportProtocol.Grpc, "http://127.0.0.1:1/")]
    [InlineData("httpProtobuf", OtlpExportProtocol.HttpProtobuf, "http://127.0.0.1:1/v1/traces")]
    public async Task Should_ConfigureNamedExporter_When_ProtocolSelected(string protocol, OtlpExportProtocol expectedProtocol, string expectedEndpoint)
    {
        using var files = new SettingsSandbox();
        var environment = Environment(true);
        environment["Telemetry__Protocol"] = protocol;
        using var host = BuildHost(files, environment);

        await host.StartAsync(TestContext.Current.CancellationToken);
        var options = host.Services.GetRequiredService<IOptionsMonitor<OtlpExporterOptions>>().Get("codeassist-traces");
        options.Protocol.ShouldBe(expectedProtocol);
        options.Endpoint.AbsoluteUri.ShouldBe(expectedEndpoint);
        options.TimeoutMilliseconds.ShouldBe(1000);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    private static Dictionary<string, string> Environment(bool enabled) => new(StringComparer.OrdinalIgnoreCase)
    {
        ["Telemetry__Enabled"] = enabled.ToString(),
        ["Telemetry__Endpoint"] = "http://127.0.0.1:1",
        ["Telemetry__ExportTimeoutSeconds"] = "1",
        ["Telemetry__Metrics"] = "false",
        ["Telemetry__Logs"] = "false",
    };

    private static IHost BuildHost(SettingsSandbox files, Dictionary<string, string> environment)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Services.AddSingleton<IEnvironmentVariables>(new DictionaryEnvironmentVariables(environment));
        builder.Services.AddSingleton(files.Locations);
        builder.Services.AddCodeAssistSettings();
        builder.AddServiceDefaults();
        return builder.Build();
    }
}