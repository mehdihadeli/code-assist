using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using CodeAssist.Tools;
using CodeAssist.Workspaces.Platform;

namespace CodeAssist.Tools.Extensions;

public sealed class TrxTestFeedback : ITestFeedback
{
    private readonly string _root;
    private readonly IProcessRunner _runner;
    private readonly IReadOnlyDictionary<string, TestExecutionProfile> _profiles;
    private readonly IFileSystem _files;
    public IReadOnlyList<string> Profiles => _profiles.Keys.Order(StringComparer.Ordinal).ToArray();

    public TrxTestFeedback(string directory, IProcessRunner runner, IEnumerable<TestExecutionProfile> profiles, IFileSystem? files = null)
    {
        _root = Path.GetFullPath(directory);
        _runner = runner;
        _files = files ?? new LocalFileSystem();
        _profiles = profiles.ToDictionary(profile => profile.Name, profile => profile with { Arguments = profile.Arguments.ToArray() }, StringComparer.Ordinal);
        if (_profiles.Count is < 1 or > 32 || _profiles.Values.Any(profile => string.IsNullOrWhiteSpace(profile.Name) || string.IsNullOrWhiteSpace(profile.Executable))) throw new ArgumentException("Supply 1-32 trusted test profiles.");
    }

    public async Task<TestRunFeedback> RunAsync(string profile, string? filter, ToolContext context, CancellationToken cancellationToken)
    {
        if (!_profiles.TryGetValue(profile, out var selected)) throw new ArgumentException("Unknown test profile.");
        if (filter?.Length > 2000 || filter?.Contains('\0') == true || filter is not null && selected.FilterArgument is null) throw new ArgumentException("Filter is unsupported or invalid.");
        var id = Guid.NewGuid();
        var folder = Folder(context.SessionId, id);
        Directory.CreateDirectory(folder);
        var arguments = selected.Arguments.Concat(selected.MicrosoftTestingPlatform ? new[] { "--report-trx", "--report-trx-filename", "report.trx" } : new[] { "--logger", "trx;LogFileName=report.trx" }).Concat(["--results-directory", folder]).ToList();
        if (filter is not null) arguments.AddRange([selected.FilterArgument!, filter]);
        var outcome = await _runner.RunAsync(new(selected.Executable, arguments, context.Workspace, context.Limits.ProcessTimeout, context.Limits.MaxProcessOutputChars, context.Limits.AllowedEnvironment), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var report = Path.Combine(folder, "report.trx");
        var cases = new List<TestCaseFeedback>();
        var status = outcome.Cancelled ? "cancelled" : outcome.TimedOut ? "timed-out" : "report-unavailable";
        if (!outcome.TimedOut && !outcome.Cancelled && File.Exists(report))
        {
            var info = new FileInfo(report);
            if (info.LinkTarget is not null || info.Length > 8 * 1024 * 1024) throw new IOException("TRX report exceeds limits or is linked.");
            using var reader = XmlReader.Create(report, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 8 * 1024 * 1024 });
            var document = XDocument.Load(reader);
            if (document.Root?.Name.LocalName != "TestRun") throw new IOException("Not a TRX test report.");
            foreach (var item in document.Descendants().Where(element => element.Name.LocalName == "UnitTestResult"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (cases.Count >= 2000) throw new IOException("TRX case capacity exceeded.");
                var name = (string?)item.Attribute("testName") ?? "unnamed";
                var result = (string?)item.Attribute("outcome") ?? "Unknown";
                cases.Add(new((string?)item.Attribute("testId") ?? name, name, result,
                    item.Descendants().FirstOrDefault(element => element.Name.LocalName == "Message")?.Value,
                    item.Descendants().FirstOrDefault(element => element.Name.LocalName == "StackTrace")?.Value));
            }
            status = cases.Count == 0 ? "empty" : outcome.ExitCode == 0 && cases.All(item => item.Outcome is "Passed" or "NotExecuted") ? "completed" : "failed";
        }
        var run = new TestRunFeedback(id, profile, status, outcome.ExitCode, cases.Count, cases.Count(item => item.Outcome == "Passed"), cases.Count(item => item.Outcome is not ("Passed" or "NotExecuted")), cases.Count(item => item.Outcome == "NotExecuted"), cases, outcome.Output);
        var json = JsonSerializer.SerializeToUtf8Bytes(run);
        if (json.Length > 8 * 1024 * 1024) throw new IOException("Structured test report exceeds its storage limit.");
        await File.WriteAllBytesAsync(Path.Combine(folder, "feedback.tmp"), json, cancellationToken);
        File.Move(Path.Combine(folder, "feedback.tmp"), Path.Combine(folder, "feedback.json"));
        return run;
    }

    public async Task<TestRunFeedback> ReadAsync(Guid id, ToolContext context, CancellationToken cancellationToken)
    {
        var path = Path.Combine(Folder(context.SessionId, id), "feedback.json");
        var info = new FileInfo(path);
        if (!info.Exists || info.LinkTarget is not null || info.Length > 8 * 1024 * 1024) throw new IOException("Test report unavailable in this session.");
        return JsonSerializer.Deserialize<TestRunFeedback>(await File.ReadAllBytesAsync(path, cancellationToken)) ?? throw new IOException("Invalid saved test report.");
    }

    public Task<IReadOnlyList<string>> RelatedAsync(string path, ToolContext context, CancellationToken cancellationToken)
    {
        var guard = new PathGuard(context.Workspace, [.. context.ProtectedGlobs, .. context.IgnoreGlobs]);
        var source = guard.Resolve(path);
        if (source.Outside || source.Protected) throw new UnauthorizedAccessException("Source path is unavailable.");
        var stem = Path.GetFileNameWithoutExtension(source.Full);
        var names = new HashSet<string>([stem + "Tests", stem + "Test", stem + ".test", stem + ".spec", "test_" + stem, stem + "_test"], StringComparer.OrdinalIgnoreCase);
        var matches = new List<string>();
        var scanned = 0;
        foreach (var candidate in _files.EnumerateFiles(context.Workspace, directory => !guard.Resolve(Path.Combine(directory, "scope-probe")).Protected))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++scanned > context.Limits.MaxSearchFiles) break;
            var resolved = guard.Resolve(candidate);
            if (!resolved.Outside && !resolved.Protected && names.Contains(Path.GetFileNameWithoutExtension(candidate))) matches.Add(resolved.Relative);
            if (matches.Count >= context.Limits.MaxSearchResults) break;
        }
        return Task.FromResult<IReadOnlyList<string>>(matches.Order(StringComparer.Ordinal).ToArray());
    }

    private string Folder(Guid session, Guid run)
    {
        if (session == Guid.Empty || run == Guid.Empty) throw new UnauthorizedAccessException("Tests require a real session and run ID.");
        var path = Path.Combine(_root, session.ToString("N"), run.ToString("N"));
        for (var ancestor = new DirectoryInfo(path); ancestor is not null; ancestor = ancestor.Parent)
            if (ancestor.LinkTarget is not null) throw new IOException("Linked test-report directories are forbidden.");
        return path;
    }
}