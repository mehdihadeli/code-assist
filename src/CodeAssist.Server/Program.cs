using System.Text.Json;
using CodeAssist.Server.Hosting;
using CodeAssist.Configuration;
using Microsoft.Extensions.Logging;

if (args is ["--help"])
{
	Console.WriteLine("Usage: CodeAssist.Server [--listen <loopback-host:port>]");
	Console.WriteLine("Authentication: Host__Listen__Http__Token (32-512 characters).");
	return 0;
}
if (args.Length != 0 && args is not ["--listen", _])
{
	Console.Error.WriteLine("Usage: CodeAssist.Server [--listen <loopback-host:port>]");
	return 2;
}

try
{
	if (Environment.GetEnvironmentVariable("CODEASSIST_MANAGED_BOOTSTRAP") == "1" &&
		(!Console.IsInputRedirected || Console.OpenStandardInput().ReadByte() != 1)) return 130;
	using var lifetime = new CancellationTokenSource();
	if (Environment.GetEnvironmentVariable("CODEASSIST_MANAGED_STDIN") == "1" && Console.IsInputRedirected)
		ThreadPool.QueueUserWorkItem(static source => MonitorParent(source), lifetime, preferLocal: false);
	var environment = new ProcessEnvironmentVariables().GetAll();
	var locations = SettingsLocations.Resolve(environment, Directory.GetCurrentDirectory());
	var flags = new Dictionary<string, string>();
	if (args is ["--listen", var address]) flags["Host.Listen.Http.Address"] = address;
	var snapshot = SettingsLoader.Load(new(locations, environment, flags));
	if (!snapshot.IsValid)
	{
		Console.Error.WriteLine("Server configuration is invalid. Use the CLI settings diagnostics to inspect it.");
		return 2;
	}
	await ServerRuntime.RunAsync(snapshot.Settings, locations, environment,
		endpoint => Console.WriteLine(JsonSerializer.Serialize(new { kind = "ready", protocol = "1", endpoint })),
		cancellation: lifetime.Token,
		configure: builder =>
		{
			builder.Logging.ClearProviders();
			builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
		});
	return 0;
}
catch (OperationCanceledException) { return 130; }
catch (Exception exception)
{
	Console.Error.WriteLine($"Server startup or execution failed ({exception.GetType().Name}). Check runtime configuration.");
	return 1;
}

static void MonitorParent(CancellationTokenSource lifetime)
{
	try
	{
		using var input = Console.OpenStandardInput();
		var buffer = new byte[1];
		while (input.Read(buffer, 0, buffer.Length) != 0)
		{
			if (buffer[0] == 2 && Environment.GetEnvironmentVariable("CODEASSIST_MANAGED_BOOTSTRAP") == "1") break;
		}
	}
	catch (IOException) { }
	finally
	{
		try { lifetime.Cancel(); }
		catch (ObjectDisposedException) { }
	}
}
