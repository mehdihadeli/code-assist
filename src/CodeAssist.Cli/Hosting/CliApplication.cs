using System.Reflection;
using CodeAssist.Cli.Commands;
using CodeAssist.Cli.Commands.Conversation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using Spectre.Console.Cli;

namespace CodeAssist.Cli.Hosting;

/// <summary>Builds the generic host, starts it, runs the Spectre command app against the host's container, then stops the host.</summary>
public static class CliApplication
{
    public static async Task<int> RunAsync(string[] args, Action<IServiceCollection>? configureServices = null)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true, Args = [] });
        builder.Logging.AddSimpleConsole(options => options.SingleLine = true);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddCodeAssistCli();
        configureServices?.Invoke(builder.Services);

        using var host = builder.Build();
        await host.StartAsync();
        try
        {
            return await RunAsync(args, host.Services);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    /// <summary>Runs the command app against an already-built provider (used by tests with a replaced console and environment).</summary>
    public static async Task<int> RunAsync(string[] args, IServiceProvider services)
    {
        var console = services.GetRequiredService<IAnsiConsole>();
        var app = new CommandApp<ChatCommand>(new HostTypeRegistrar(services));
        app.Configure(config => Configure(config, services, console));
        return await app.RunAsync(args);
    }

    private static void Configure(IConfigurator config, IServiceProvider services, IAnsiConsole console)
    {
        config.SetApplicationName("code-assist");
        config.SetApplicationVersion(typeof(CliApplication).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0");
        config.ConfigureConsole(console);
        config.SetInterceptor(services.GetRequiredService<GlobalOverridesInterceptor>());
        config.SetExceptionHandler((exception, _) => HandleException(console, exception));

        foreach (var group in services.GetServices<ICommandGroup>().OrderBy(g => g.Order))
            group.Register(config);
    }

    private static int HandleException(IAnsiConsole console, Exception exception)
    {
        switch (exception)
        {
            case UsageException usage:
                console.MarkupLine($"[red]error[/] {Markup.Escape(usage.Message)}");
                return usage.ExitCode;
            case CommandRuntimeException runtime:
                console.MarkupLine($"[red]error[/] {Markup.Escape(runtime.Message)}");
                return ExitCodes.UsageOrConfig;
            case OperationCanceledException:
                return ExitCodes.Interrupted;
            default:
                console.MarkupLine($"[red]error[/] {Markup.Escape(exception.Message)}");
                console.MarkupLine("[grey]Run with CODE_ASSIST_DEBUG=1 for the full stack trace.[/]");
                if (Environment.GetEnvironmentVariable("CODE_ASSIST_DEBUG") == "1") console.WriteException(exception);
                return ExitCodes.Failure;
        }
    }
}
