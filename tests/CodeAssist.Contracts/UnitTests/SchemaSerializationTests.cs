using CodeAssist.Persistence;
using System.Text.Json;
using CodeAssist.Contracts;
using Shouldly;

namespace CodeAssist.Contracts.UnitTests;

public sealed class SchemaSerializationTests
{
    [Fact]
    public void JournalEntryRoundTripsWithDiscriminator()
    {
        JournalEntry entry = new PromptSubmitted(Guid.NewGuid(), 3, DateTimeOffset.UnixEpoch, "hello");

        var json = JsonSerializer.Serialize(entry);
        var back = JsonSerializer.Deserialize<JournalEntry>(json);

        json.ShouldContain("\"type\":\"prompt.submitted\"");
        back.ShouldBe(entry);
    }

    [Fact]
    public void IntentRoundTripsAndCarriesItsIdempotencyKey()
    {
        var intentId = Guid.NewGuid();
        Intent intent = new ResolveApproval(Guid.NewGuid(), intentId, Guid.NewGuid(), ApprovalDecision.Allow, ApprovalGrant.Session);

        var back = JsonSerializer.Deserialize<Intent>(JsonSerializer.Serialize(intent));

        back.ShouldBe(intent);
        back!.IntentId.ShouldBe(intentId);
    }
}
