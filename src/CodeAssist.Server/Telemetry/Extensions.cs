using System.Diagnostics;
using CodeAssist.Tools.Diagnostics;
using CodeAssist.Server.Telemetry;
using CodeAssist.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Microsoft.Extensions.Hosting;

public static class Extensions
{
    private const string HealthEndpointPath = "/health";
    private const string AlivenessEndpointPath = "/alive";

    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.ConfigureOpenTelemetry();

        builder.AddDefaultHealthChecks();

        return builder;
    }

    public static TBuilder ConfigureOpenTelemetry<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Services.TryAddSingleton(provider => new TelemetryExportConfiguration(
            provider.GetRequiredService<ISettingsProvider>().Current.Settings.Telemetry,
            provider.GetRequiredService<IEnvironmentVariables>().GetAll()));
        builder.Logging.AddOpenTelemetry();
        builder.Logging.AddFilter<OpenTelemetryLoggerProvider>((category, level) =>
            category == RuntimeTelemetry.SourceName && level >= LogLevel.Information);

        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics => metrics.AddMeter(RuntimeTelemetry.SourceName)
                .SetResourceBuilder(Resource())
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddOtlpExporter("codeassist-metrics", static (_, _) => { }))
            .WithTracing(tracing => tracing.SetResourceBuilder(Resource())
                .AddSource(RuntimeTelemetry.SourceName)
                .AddAspNetCoreInstrumentation(options => options.Filter = context =>
                    !context.Request.Path.StartsWithSegments(HealthEndpointPath)
                    && !context.Request.Path.StartsWithSegments(AlivenessEndpointPath))
                .AddHttpClientInstrumentation()
                .AddProcessor(new MetadataTraceProcessor())
                .AddOtlpExporter("codeassist-traces", static _ => { }));
        builder.Services.ConfigureOpenTelemetryMeterProvider((provider, metrics) =>
            {
                var configuration = provider.GetRequiredService<TelemetryExportConfiguration>();
                if (!configuration.Enabled || !configuration.Metrics)
                    metrics.AddView(_ => MetricStreamConfiguration.Drop);
            });
        builder.Services.ConfigureOpenTelemetryTracerProvider((provider, tracing) =>
            {
                var configuration = provider.GetRequiredService<TelemetryExportConfiguration>();
                tracing.SetSampler(configuration.Enabled && configuration.Traces
                    ? new ParentBasedSampler(new TraceIdRatioBasedSampler(configuration.TraceSampleRatio))
                    : new AlwaysOffSampler());
            });

        builder.AddOpenTelemetryExporters();

        return builder;
    }

    private static TBuilder AddOpenTelemetryExporters<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Services.AddOptions<OtlpExporterOptions>("codeassist-traces").Configure<TelemetryExportConfiguration>((options, configuration) =>
        {
            if (configuration.Enabled && configuration.Traces) configuration.Configure(options, "traces");
        });
        builder.Services.AddOptions<OtlpExporterOptions>("codeassist-metrics").Configure<TelemetryExportConfiguration>((options, configuration) =>
        {
            if (configuration.Enabled && configuration.Metrics) configuration.Configure(options, "metrics");
        });
        builder.Services.AddOptions<MetricReaderOptions>("codeassist-metrics").Configure<TelemetryExportConfiguration>((options, configuration) =>
        {
            if (!configuration.Enabled || !configuration.Metrics) return;
            options.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = configuration.MetricIntervalMilliseconds;
            options.PeriodicExportingMetricReaderOptions.ExportTimeoutMilliseconds = configuration.TimeoutMilliseconds;
        });
        builder.Services.AddOptions<OpenTelemetryLoggerOptions>().Configure<TelemetryExportConfiguration>((options, configuration) =>
        {
            options.IncludeScopes = false;
            options.IncludeFormattedMessage = false;
            options.SetResourceBuilder(Resource());
            if (configuration.Enabled && configuration.Logs)
                options.AddOtlpExporter((exporter, processor) =>
                {
                    configuration.Configure(exporter, "logs");
                    processor.BatchExportProcessorOptions = new()
                    {
                        MaxQueueSize = 1024, MaxExportBatchSize = 128,
                        ScheduledDelayMilliseconds = 1000, ExporterTimeoutMilliseconds = configuration.TimeoutMilliseconds,
                    };
                });
        });
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, OperationLogService>());

        return builder;
    }

    public static TBuilder AddDefaultHealthChecks<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Services.AddHealthChecks()
            // Add a default liveness check to ensure app is responsive
            .AddCheck("self", () => HealthCheckResult.Healthy(), ["live"]);

        return builder;
    }

    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        // Adding health checks endpoints to applications in non-development environments has security implications.
        // See https://aka.ms/aspire/healthchecks for details before enabling these endpoints in non-development environments.
        if (app.Environment.IsDevelopment())
        {
            // All health checks must pass for app to be considered ready to accept traffic after starting
            app.MapHealthChecks(HealthEndpointPath);

            // Only health checks tagged with the "live" tag must pass for app to be considered alive
            app.MapHealthChecks(AlivenessEndpointPath, new HealthCheckOptions
            {
                Predicate = r => r.Tags.Contains("live")
            });
        }

        return app;
    }

    private static ResourceBuilder Resource() => ResourceBuilder.CreateEmpty()
        .AddService("code-assist", serviceVersion: typeof(Extensions).Assembly.GetName().Version?.ToString());

    private sealed class MetadataTraceProcessor : BaseProcessor<Activity>
    {
        public override void OnEnd(Activity activity)
        {
            if (activity.Source.Name == RuntimeTelemetry.SourceName) return;
            foreach (var tag in activity.TagObjects.ToArray())
                if (tag.Key is not ("http.request.method" or "http.response.status_code" or "network.protocol.version" or "error.type"))
                    activity.SetTag(tag.Key, null);
            activity.SetStatus(activity.Status);
        }
    }

    private sealed class OperationLogService(TelemetryExportConfiguration configuration, ILoggerFactory loggerFactory) : IHostedService, IDisposable
    {
        private readonly ILogger _logger = loggerFactory.CreateLogger(RuntimeTelemetry.SourceName);

        public Task StartAsync(CancellationToken cancellationToken)
        {
            if (configuration.Enabled && configuration.Logs) RuntimeTelemetry.Completed += Write;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) { Dispose(); return Task.CompletedTask; }
        public void Dispose() => RuntimeTelemetry.Completed -= Write;

        private void Write(OperationRecord record) => _logger.LogInformation(
            "Operation {Operation} ended with {Outcome}; duration {DurationSeconds}; error type {ErrorType}",
            record.Operation, record.Outcome, record.DurationSeconds, record.ErrorType);
    }
}
