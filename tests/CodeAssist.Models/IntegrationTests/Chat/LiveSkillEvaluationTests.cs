using CodeAssist.Extensions.Skills;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using CodeAssist.Tools;
using CodeAssist.Sessions;
using CodeAssist.Testing.Live;
using CodeAssist.Workspaces;
using Shouldly;

namespace CodeAssist.Models.IntegrationTests.Chat;

public sealed class LiveSkillEvaluationTests
{
    private static readonly LiveTestConfiguration Config = LiveTestConfiguration.Load();
    private static readonly Lazy<LiveProviderConfiguration> Providers = new(() => LiveProviderConfiguration.Load(Config));

    public static IEnumerable<TheoryDataRow<string, string, string>> Cases()
    {
        foreach (var scenario in new[] { "isolated", "coexistence", "unrelated", "near-miss" })
        {
            if (!Config.Enabled)
            {
                yield return new("", "", scenario) { TestDisplayName = scenario + ": live evaluations disabled" };
                continue;
            }
            var models = Providers.Value.Models.Where(model => model.Tools).ToArray();
            if (models.Length == 0) yield return new("", "", scenario) { TestDisplayName = scenario + ": no tool-capable models configured" };
            foreach (var model in models)
                yield return new(model.ProviderId, model.ModelId, scenario) { TestDisplayName = $"{model.ProviderId}/{model.ModelId}: {scenario}" };
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Should_MeetSkillBehaviorRubric_When_ConfiguredModelRunsEvaluation(string providerId, string modelId, string scenario)
    {
        Assert.SkipUnless(Config.Enabled, $"Live evaluations are off. Set {LiveTestConfiguration.EnabledVariable}=1; real provider charges apply.");
        Assert.SkipWhen(providerId.Length == 0, "No enabled tool-capable chat models configured.");
        var code = Guid.NewGuid().ToString("N");
        var prompt = scenario switch
        {
            "unrelated" => "What is 2 + 2? Return only the number.",
            "near-miss" => "Meridian is a fictional project's name. This is not a release review. Reply only ACK.",
            _ => "Review this change under the Meridian release process: retry delay now has an upper bound. Return the prescribed review result.",
        };
        var catalog = new EvaluationCatalog(code, scenario == "coexistence");
        string? baseline = null;
        if (scenario == "isolated")
        {
            using var withoutSkills = new AgentSessionService(_ => Providers.Value.CreateClient(providerId, modelId));
            var baselineSession = await withoutSkills.CreateAsync(providerId + "/" + modelId, TestContext.Current.CancellationToken);
            baseline = (await withoutSkills.SendAsync(baselineSession.Id, prompt, cancellationToken: TestContext.Current.CancellationToken)).Text;
            baseline.ShouldNotContain(code);
        }
        using var sessions = new AgentSessionService(_ => Providers.Value.CreateClient(providerId, modelId), instructions: SessionSkillTools.Instructions,
            tools: (sessionId, _) => new SessionSkillTools(catalog, new() { Workspace = Directory.GetCurrentDirectory(), SessionId = sessionId }).Create());
        var session = await sessions.CreateAsync(providerId + "/" + modelId, TestContext.Current.CancellationToken);
        var elapsed = Stopwatch.StartNew();
        var result = await sessions.SendAsync(session.Id, prompt, cancellationToken: TestContext.Current.CancellationToken);
        var expected = scenario switch
        {
            "unrelated" => "4",
            "near-miss" => "ACK",
            _ => $"REVIEW {code}\nVALIDATED",
        };
        var matches = result.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Trim() == expected;
        TestContext.Current.TestOutputHelper?.WriteLine(JsonSerializer.Serialize(new
        {
            model = providerId + "/" + modelId, scenario, outputMatches = matches,
            loaded = catalog.Loaded.ToArray(), resources = catalog.Resources.ToArray(),
            baselineMatched = baseline is null ? (bool?)null : baseline.Trim() == expected,
            elapsedMilliseconds = elapsed.ElapsedMilliseconds,
        }));
        matches.ShouldBeTrue("Model must meet the exact scenario output rubric.");
        if (scenario is "unrelated" or "near-miss")
        {
            catalog.Loaded.ShouldBeEmpty();
            catalog.Resources.ShouldBeEmpty();
        }
        else
        {
            catalog.Loaded.ShouldContain("release-audit");
            catalog.Loaded.ShouldNotContain("incident-audit");
            catalog.Resources.ShouldContain("release-audit/acceptance.txt");
        }
    }

    private sealed class EvaluationCatalog : ISkillCatalog
    {
        private readonly BundledSkillCatalog _inner;
        public ConcurrentQueue<string> Loaded { get; } = new();
        public ConcurrentQueue<string> Resources { get; } = new();

        public EvaluationCatalog(string code, bool distractor)
        {
            var bundles = new List<SkillBundle>
            {
                new(new("release-audit", "Reviews changes under the Meridian release process. Use for Meridian release reviews, not arithmetic, acknowledgments or general project discussion."),
                    "For a Meridian release review, read acceptance.txt. Return exactly two lines: REVIEW followed by a space and the verification code from that file; then VALIDATED. Do not invent the code or add commentary.",
                    new Dictionary<string, string> { ["acceptance.txt"] = "Verification code: " + code }),
            };
            if (distractor) bundles.Add(new(new("incident-audit", "Reviews active production incidents under the Meridian incident process, not release reviews."),
                "For an active incident, return INCIDENT. This guidance does not apply to release reviews."));
            _inner = new(bundles);
        }

        public IReadOnlyList<SkillSummary> Index => _inner.Index;
        public Task<SkillContent?> LoadAsync(string id, ToolContext context, CancellationToken cancellationToken) => InvokeAsync(id, new(), context, cancellationToken);
        public Task<SkillContent?> InvokeAsync(string id, SkillInvocation invocation, ToolContext context, CancellationToken cancellationToken)
        {
            Loaded.Enqueue(id);
            return _inner.InvokeAsync(id, invocation, context, cancellationToken);
        }
        public Task<string?> ReadFileAsync(string id, string path, ToolContext context, CancellationToken cancellationToken)
        {
            Resources.Enqueue(id + "/" + path);
            return _inner.ReadFileAsync(id, path, context, cancellationToken);
        }
    }
}