using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeAssist.Contracts;
using CodeAssist.Persistence;
using Shouldly;

namespace CodeAssist.Sessions.UnitTests;

public sealed class FileConversationStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "conversation-store-tests", Guid.NewGuid().ToString("N"));
    private static readonly CancellationToken Token = CancellationToken.None;

    [Fact]
    public async Task Should_PreserveStableIdentityArchiveAndUsage_When_StoreRestarts()
    {
        var id = Guid.NewGuid();
        using (var owner = new FileConversationStore(_directory))
        {
            IConversationStore store = owner;
            (await store.ListAsync(Token)).ShouldBeEmpty();
            var committed = await store.CommitAsync(Snapshot(id) with { Revision = 900 }, 0, Token);
            committed.Id.ShouldBe(id);
            committed.Revision.ShouldBe(1);
            await store.CommitAsync(Snapshot(id) with { Title = "Renamed", Archived = true, InputTokens = 55 }, 1, Token);
        }

        using var restarted = new FileConversationStore(_directory);
        var current = (await restarted.ListAsync(Token)).ShouldHaveSingleItem();
        current.Id.ShouldBe(id);
        current.Revision.ShouldBe(2);
        current.Title.ShouldBe("Renamed");
        current.Archived.ShouldBeTrue();
        current.InputTokens.ShouldBe(55);
        current.OutputTokens.ShouldBe(23);
        current.CompletedTurns.ShouldBe(4);
        current.FailedTurns.ShouldBe(2);
        current.TokensReported.ShouldBeTrue();
        current.Archive.Version.ShouldBe(1);
        current.Archive.Model.ShouldBe("provider/model");
        current.Archive.CreatedAt.ShouldBe(DateTimeOffset.Parse("2026-01-02T03:04:05Z"));
        current.Archive.Messages.Select(message => message.Sequence).ShouldBe([1, 2]);
        current.Archive.Messages.Select(message => message.Role).ShouldBe(["user", "assistant"]);
        current.Archive.Messages.Select(message => message.Text).ShouldBe(["question\n\"quoted\" \u03bb", "answer"]);
        current.Archive.Messages.Select(message => message.CreatedAt).ShouldAllBe(value => value == current.Archive.CreatedAt);
        current.Archive.State.GetProperty("context").GetString().ShouldBe("durable");
        (await restarted.ReadAsync(id, 1, Token)).Title.ShouldBe("Original");
        (await restarted.RevisionsAsync(id, Token)).ShouldBe([2L, 1L]);
        Directory.GetFiles(_directory, "*.tmp", SearchOption.AllDirectories).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(2)]
    public async Task Should_PreserveCommittedState_When_ExpectedRevisionIsStale(long expectedRevision)
    {
        var id = Guid.NewGuid();
        using var store = new FileConversationStore(_directory);
        await store.CommitAsync(Snapshot(id), 0, Token);

        await Should.ThrowAsync<JournalConflictException>(() => store.CommitAsync(Snapshot(id) with { Title = "Rejected" }, expectedRevision, Token));
        await Should.ThrowAsync<JournalConflictException>(() => store.RemoveAsync(id, expectedRevision, Token));

        (await store.ListAsync(Token)).ShouldHaveSingleItem().Title.ShouldBe("Original");
        (await store.RevisionsAsync(id, Token)).ShouldBe([1L]);
        (await store.CommitAsync(Snapshot(id) with { Title = "Accepted" }, 1, Token)).Revision.ShouldBe(2);
        (await store.ReadAsync(id, 2, Token)).Title.ShouldBe("Accepted");
    }

    [Theory]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(15)]
    public async Task Should_RetainExactRewindSnapshots_When_CheckpointCountReachesRetention(int count)
    {
        var id = Guid.NewGuid();
        using (var store = new FileConversationStore(_directory))
        {
            for (var revision = 1; revision <= count; revision++)
                await store.CommitAsync(Snapshot(id) with { Title = $"Turn {revision}", CompletedTurns = revision }, revision - 1, Token);
        }

        using var restarted = new FileConversationStore(_directory);
        var retained = Enumerable.Range(Math.Max(1, count - 11), Math.Min(count, 12)).Select(value => (long)value).Reverse().ToArray();
        (await restarted.RevisionsAsync(id, Token)).ShouldBe(retained);
        foreach (var revision in retained)
        {
            var checkpoint = await restarted.ReadAsync(id, revision, Token);
            checkpoint.Id.ShouldBe(id);
            checkpoint.Revision.ShouldBe(revision);
            checkpoint.Title.ShouldBe($"Turn {revision}");
            checkpoint.CompletedTurns.ShouldBe(revision);
        }
        if (count > 12)
            await Should.ThrowAsync<KeyNotFoundException>(() => restarted.ReadAsync(id, count - 12, Token));
        (await restarted.ListAsync(Token)).ShouldHaveSingleItem().Revision.ShouldBe(count);
    }

    [Fact]
    public async Task Should_RefuseSecondOwnerAndPermitNextOwner_When_LeaseIsReleased()
    {
        var id = Guid.NewGuid();
        using (var first = new FileConversationStore(_directory))
        {
            await first.CommitAsync(Snapshot(id), 0, Token);
            Should.Throw<IOException>(() =>
            {
                using var second = new FileConversationStore(_directory);
            });
            using var independent = new FileConversationStore(Path.Combine(_directory, "independent"));
            (await independent.ListAsync(Token)).ShouldBeEmpty();
            (await first.ReadAsync(id, 1, Token)).Title.ShouldBe("Original");
        }

        using var next = new FileConversationStore(_directory);
        (await next.ListAsync(Token)).ShouldHaveSingleItem().Id.ShouldBe(id);
        (await next.CommitAsync(Snapshot(id), 1, Token)).Revision.ShouldBe(2);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Should_NotResurrectDeletedSnapshots_When_StoreRestartsWithOrWithoutOrphanHistory(bool leaveOrphans)
    {
        var id = Guid.NewGuid();
        var survivor = Guid.NewGuid();
        var history = Path.Combine(_directory, id.ToString("N"));
        using (var store = new FileConversationStore(_directory))
        {
            await store.CommitAsync(Snapshot(id), 0, Token);
            await store.CommitAsync(Snapshot(id) with { Title = "Deleted second turn" }, 1, Token);
            await store.CommitAsync(Snapshot(survivor) with { Title = "Survivor" }, 0, Token);
            var oldFiles = Directory.GetFiles(history).ToDictionary(path => Path.GetFileName(path), File.ReadAllBytes);
            await store.RemoveAsync(id, 2, Token);
            File.Exists(Path.Combine(_directory, id.ToString("N") + ".json")).ShouldBeFalse();
            Directory.Exists(history).ShouldBeFalse();
            if (leaveOrphans)
            {
                Directory.CreateDirectory(history);
                foreach (var file in oldFiles) await File.WriteAllBytesAsync(Path.Combine(history, file.Key), file.Value, Token);
            }
        }

        using var restarted = new FileConversationStore(_directory);
        (await restarted.ListAsync(Token)).ShouldHaveSingleItem().Id.ShouldBe(survivor);
        await Should.ThrowAsync<KeyNotFoundException>(() => restarted.ReadAsync(id, 1, Token));
        await Should.ThrowAsync<KeyNotFoundException>(() => restarted.RevisionsAsync(id, Token));
        await Should.ThrowAsync<KeyNotFoundException>(() => restarted.RemoveAsync(id, 2, Token));
        (await restarted.CommitAsync(Snapshot(id) with { Title = "Replacement" }, 0, Token)).Revision.ShouldBe(1);
        (await restarted.RevisionsAsync(id, Token)).ShouldBe([1L]);
        (await restarted.ReadAsync(id, 1, Token)).Title.ShouldBe("Replacement");
        await Should.ThrowAsync<KeyNotFoundException>(() => restarted.ReadAsync(id, 2, Token));
        (await restarted.ReadAsync(survivor, 1, Token)).Title.ShouldBe("Survivor");
    }

    [Theory]
    [InlineData("list")]
    [InlineData("commit")]
    [InlineData("read")]
    [InlineData("revisions")]
    [InlineData("remove")]
    public async Task Should_PreserveStateAndReleaseGate_When_OperationIsAlreadyCanceled(string operation)
    {
        var id = Guid.NewGuid();
        using var store = new FileConversationStore(_directory);
        await store.CommitAsync(Snapshot(id), 0, Token);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() => InvokeAsync(store, operation, id, canceled.Token));

        (await store.ListAsync(Token)).ShouldHaveSingleItem().Title.ShouldBe("Original");
        (await store.RevisionsAsync(id, Token)).ShouldBe([1L]);
        (await store.ReadAsync(id, 1, Token)).Revision.ShouldBe(1);
        (await store.CommitAsync(Snapshot(id), 1, Token)).Revision.ShouldBe(2);
        Directory.GetFiles(_directory, "*.tmp", SearchOption.AllDirectories).ShouldBeEmpty();
    }

    [Fact]
    public async Task Should_WriteNothing_When_FirstCommitIsAlreadyCanceled()
    {
        using var store = new FileConversationStore(_directory);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var id = Guid.NewGuid();

        await Should.ThrowAsync<OperationCanceledException>(() => store.CommitAsync(Snapshot(id), 0, canceled.Token));

        (await store.ListAsync(Token)).ShouldBeEmpty();
        Directory.GetFiles(_directory, "*.json", SearchOption.AllDirectories).ShouldBeEmpty();
        (await store.CommitAsync(Snapshot(id), 0, Token)).Revision.ShouldBe(1);
    }

    [Theory]
    [InlineData("read")]
    [InlineData("revisions")]
    [InlineData("remove")]
    public async Task Should_RejectMissingConversationWithoutCreatingFiles_When_IdIsUnknown(string operation)
    {
        using var store = new FileConversationStore(_directory);
        await Should.ThrowAsync<KeyNotFoundException>(() => InvokeAsync(store, operation, Guid.NewGuid(), Token));
        (await store.ListAsync(Token)).ShouldBeEmpty();
        Directory.GetFiles(_directory, "*.json", SearchOption.AllDirectories).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("commit")]
    [InlineData("read")]
    [InlineData("revisions")]
    [InlineData("remove")]
    public async Task Should_RejectEmptyIdentityWithoutCreatingSnapshots_When_IdIsEmpty(string operation)
    {
        using var store = new FileConversationStore(_directory);
        await Should.ThrowAsync<ArgumentException>(() => InvokeAsync(store, operation, Guid.Empty, Token));
        (await store.ListAsync(Token)).ShouldBeEmpty();
        Directory.GetFiles(_directory, "*.json", SearchOption.AllDirectories).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(0L)]
    [InlineData(2L)]
    [InlineData(long.MaxValue)]
    public async Task Should_RejectRevisionOutsideCommittedRange_When_CheckpointIsRequested(long revision)
    {
        var id = Guid.NewGuid();
        using var store = new FileConversationStore(_directory);
        await store.CommitAsync(Snapshot(id), 0, Token);

        await Should.ThrowAsync<KeyNotFoundException>(() => store.ReadAsync(id, revision, Token));

        (await store.ReadAsync(id, 1, Token)).Title.ShouldBe("Original");
        (await store.RevisionsAsync(id, Token)).ShouldBe([1L]);
    }

    [Fact]
    public async Task Should_RejectNonzeroExpectedRevision_When_ConversationDoesNotExist()
    {
        var id = Guid.NewGuid();
        using var store = new FileConversationStore(_directory);
        await Should.ThrowAsync<JournalConflictException>(() => store.CommitAsync(Snapshot(id), 1, Token));
        (await store.ListAsync(Token)).ShouldBeEmpty();
        (await store.CommitAsync(Snapshot(id), 0, Token)).Revision.ShouldBe(1);
    }

    [Fact]
    public async Task Should_CommitOnlyOneContender_When_WritersShareExpectedRevision()
    {
        var id = Guid.NewGuid();
        using var store = new FileConversationStore(_directory);
        await store.CommitAsync(Snapshot(id), 0, Token);
        static async Task<string?> AttemptAsync(FileConversationStore target, Guid conversationId, string title)
        {
            try
            {
            await target.CommitAsync(Snapshot(conversationId) with { Title = title }, 1, Token);
                return title;
            }
            catch (JournalConflictException) { return null; }
        }

        var results = await Task.WhenAll(AttemptAsync(store, id, "First writer"), AttemptAsync(store, id, "Second writer"));

        results.Count(value => value is null).ShouldBe(1);
        var winner = results.Where(value => value is not null).ShouldHaveSingleItem();
        (await store.ReadAsync(id, 2, Token)).Title.ShouldBe(winner);
        (await store.RevisionsAsync(id, Token)).ShouldBe([2L, 1L]);
        (await store.ReadAsync(id, 1, Token)).Title.ShouldBe("Original");
    }

    [Fact]
    public async Task Should_AssignNewRevisionAndPreserveLaterHistory_When_RetainedSnapshotIsRecommitted()
    {
        var id = Guid.NewGuid();
        using var store = new FileConversationStore(_directory);
        await store.CommitAsync(Snapshot(id), 0, Token);
        await store.CommitAsync(Snapshot(id) with { Title = "Later", CompletedTurns = 9 }, 1, Token);
        var rewind = await store.ReadAsync(id, 1, Token);

        var restored = await store.CommitAsync(rewind, 2, Token);

        restored.Id.ShouldBe(id);
        restored.Revision.ShouldBe(3);
        restored.Title.ShouldBe("Original");
        restored.CompletedTurns.ShouldBe(4);
        (await store.ReadAsync(id, 2, Token)).Title.ShouldBe("Later");
        (await store.RevisionsAsync(id, Token)).ShouldBe([3L, 2L, 1L]);
        (await store.ListAsync(Token)).ShouldHaveSingleItem().Revision.ShouldBe(3);
    }

    [Theory]
    [InlineData("truncated")]
    [InlineData("null")]
    [InlineData("missing-value")]
    [InlineData("invalid-type")]
    [InlineData("checksum")]
    [InlineData("format")]
    [InlineData("empty-id")]
    [InlineData("zero-revision")]
    [InlineData("oversized")]
    public async Task Should_FailClosedWithoutChangingFiles_When_LatestSnapshotIsCorrupt(string damage)
    {
        var id = Guid.NewGuid();
        using (var store = new FileConversationStore(_directory))
            await store.CommitAsync(Snapshot(id), 0, Token);
        var latest = Path.Combine(_directory, id.ToString("N") + ".json");
        await DamageAsync(latest, damage);
        var corruptBytes = await File.ReadAllBytesAsync(latest, Token);

        using var restarted = new FileConversationStore(_directory);
        foreach (var operation in new[] { "list", "read", "revisions", "commit", "remove" })
        {
            await Should.ThrowAsync<IOException>(() => InvokeAsync(restarted, operation, id, Token));
            (await File.ReadAllBytesAsync(latest, Token)).ShouldBe(corruptBytes);
        }
        Directory.GetFiles(_directory, "*.tmp", SearchOption.AllDirectories).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("truncated")]
    [InlineData("checksum")]
    [InlineData("foreign-id")]
    [InlineData("wrong-revision")]
    [InlineData("oversized")]
    [InlineData("missing")]
    public async Task Should_RejectDamagedRewindWithoutLosingLatest_When_HistorySnapshotIsInvalid(string damage)
    {
        var id = Guid.NewGuid();
        using (var store = new FileConversationStore(_directory))
        {
            await store.CommitAsync(Snapshot(id), 0, Token);
            await store.CommitAsync(Snapshot(id) with { Title = "Current" }, 1, Token);
        }
        var history = Path.Combine(_directory, id.ToString("N"), 1L.ToString("D20") + ".json");
        await DamageAsync(history, damage);

        using var restarted = new FileConversationStore(_directory);
        if (damage == "missing")
            await Should.ThrowAsync<KeyNotFoundException>(() => restarted.ReadAsync(id, 1, Token));
        else
            await Should.ThrowAsync<IOException>(() => restarted.ReadAsync(id, 1, Token));
        (await restarted.ListAsync(Token)).ShouldHaveSingleItem().Title.ShouldBe("Current");
        (await restarted.ReadAsync(id, 2, Token)).Title.ShouldBe("Current");
        (await restarted.CommitAsync(Snapshot(id) with { Title = "Recovered" }, 2, Token)).Revision.ShouldBe(3);
    }

    [Theory]
    [InlineData("list")]
    [InlineData("read")]
    [InlineData("revisions")]
    [InlineData("commit")]
    [InlineData("remove")]
    public async Task Should_RejectForeignLatestWithoutMutation_When_ValidSnapshotBelongsToAnotherIdentity(string operation)
    {
        var id = Guid.NewGuid();
        var donor = Guid.NewGuid();
        using (var store = new FileConversationStore(_directory))
        {
            await store.CommitAsync(Snapshot(id), 0, Token);
            await store.CommitAsync(Snapshot(donor) with { Title = "Foreign" }, 0, Token);
        }
        var latest = Path.Combine(_directory, id.ToString("N") + ".json");
        var donorPath = Path.Combine(_directory, donor.ToString("N") + ".json");
        File.Copy(donorPath, latest, overwrite: true);
        var foreignBytes = await File.ReadAllBytesAsync(latest, Token);

        using var restarted = new FileConversationStore(_directory);
        await Should.ThrowAsync<IOException>(() => InvokeAsync(restarted, operation, id, Token));
        (await File.ReadAllBytesAsync(latest, Token)).ShouldBe(foreignBytes);
        (await restarted.ReadAsync(donor, 1, Token)).Title.ShouldBe("Foreign");
        Directory.GetFiles(Path.Combine(_directory, id.ToString("N")), "*.json").Length.ShouldBe(1);
    }

    [Fact]
    public async Task Should_IgnoreUncommittedFiles_When_HistoryIsAheadOfLatestSnapshot()
    {
        var id = Guid.NewGuid();
        var latest = Path.Combine(_directory, id.ToString("N") + ".json");
        using (var store = new FileConversationStore(_directory))
        {
            await store.CommitAsync(Snapshot(id), 0, Token);
            var committedBytes = await File.ReadAllBytesAsync(latest, Token);
            await store.CommitAsync(Snapshot(id) with { Title = "Uncommitted future" }, 1, Token);
            await File.WriteAllBytesAsync(latest, committedBytes, Token);
        }
        await File.WriteAllTextAsync(latest + ".interrupted.tmp", "{", Token);

        using var restarted = new FileConversationStore(_directory);
        (await restarted.ListAsync(Token)).ShouldHaveSingleItem().Title.ShouldBe("Original");
        (await restarted.RevisionsAsync(id, Token)).ShouldBe([1L]);
        await Should.ThrowAsync<KeyNotFoundException>(() => restarted.ReadAsync(id, 2, Token));
        (await restarted.CommitAsync(Snapshot(id) with { Title = "Committed replacement" }, 1, Token)).Revision.ShouldBe(2);
        (await restarted.ReadAsync(id, 2, Token)).Title.ShouldBe("Committed replacement");
        (await restarted.RevisionsAsync(id, Token)).ShouldBe([2L, 1L]);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Should_EnforceExactCheckpointByteLimit_When_SerializedEnvelopeReachesBoundary(int offset)
    {
        var id = Guid.NewGuid();
        var latest = Path.Combine(_directory, id.ToString("N") + ".json");
        var expectedTitleLength = 8;
        using (var store = new FileConversationStore(_directory))
        {
            await store.CommitAsync(Snapshot(id) with { Title = "" }, 0, Token);
            var overhead = new FileInfo(latest).Length;
            await store.RemoveAsync(id, 1, Token);
            var paddingLength = checked((int)(8 * 1024 * 1024 + offset - overhead));
            var candidate = Snapshot(id) with { Title = new string('x', paddingLength) };

            if (offset > 0)
            {
                await Should.ThrowAsync<IOException>(() => store.CommitAsync(candidate, 0, Token));
                (await store.ListAsync(Token)).ShouldBeEmpty();
                Directory.GetFiles(_directory, "*.json", SearchOption.AllDirectories).ShouldBeEmpty();
                (await store.CommitAsync(Snapshot(id), 0, Token)).Revision.ShouldBe(1);
            }
            else
            {
                expectedTitleLength = paddingLength;
                (await store.CommitAsync(candidate, 0, Token)).Revision.ShouldBe(1);
                new FileInfo(latest).Length.ShouldBe(8 * 1024 * 1024 + offset);
            }
        }

        using var restarted = new FileConversationStore(_directory);
        var restored = await restarted.ReadAsync(id, 1, Token);
        restored.Id.ShouldBe(id);
        restored.Revision.ShouldBe(1);
        restored.Title.Length.ShouldBe(expectedTitleLength);
        restored.Title.ShouldBe(offset > 0 ? "Original" : new string('x', expectedTitleLength));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Should_EnforceExactStorageByteLimit_When_CommitWouldReachCapacity(int offset)
    {
        var id = Guid.NewGuid();
        using var store = new FileConversationStore(_directory);
        await store.CommitAsync(Snapshot(id), 0, Token);
        var latest = Path.Combine(_directory, id.ToString("N") + ".json");
        var checkpointBytes = new FileInfo(latest).Length;
        var occupied = Directory.GetFiles(_directory, "*.json", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length);
        var quotaDirectory = Path.Combine(_directory, "capacity-fixture");
        Directory.CreateDirectory(quotaDirectory);
        var quotaPath = Path.Combine(quotaDirectory, "reserved.json");
        using (var quota = File.Create(quotaPath))
            quota.SetLength(256L * 1024 * 1024 + offset - occupied - checkpointBytes * 2);

        if (offset > 0)
        {
            await Should.ThrowAsync<IOException>(() => store.CommitAsync(Snapshot(id), 1, Token));
            (await store.ListAsync(Token)).ShouldHaveSingleItem().Revision.ShouldBe(1);
            (await store.RevisionsAsync(id, Token)).ShouldBe([1L]);
            File.Delete(quotaPath);
            (await store.CommitAsync(Snapshot(id), 1, Token)).Revision.ShouldBe(2);
        }
        else
        {
            (await store.CommitAsync(Snapshot(id), 1, Token)).Revision.ShouldBe(2);
            (await store.ReadAsync(id, 2, Token)).Title.ShouldBe("Original");
        }
        Directory.GetFiles(_directory, "*.tmp", SearchOption.AllDirectories).ShouldBeEmpty();
    }

    [Fact]
    public async Task Should_ListEveryStableIdentityThenRejectOverflow_When_ListingReachesCapacity()
    {
        using var store = new FileConversationStore(_directory);
        for (var index = 1; index <= 10001; index++)
        {
            var id = new Guid(index, 0, 0, new byte[8]);
            var checkpoint = Snapshot(id) with { Revision = 1, Title = $"Session {index}", Archived = index % 2 == 0 };
            await WriteEnvelopeAsync(Path.Combine(_directory, id.ToString("N") + ".json"), checkpoint);
            if (index is 9999 or 10000)
            {
                var listed = await store.ListAsync(Token);
                listed.Count.ShouldBe(index);
                listed.Select(value => value.Id).Order().ShouldBe(Enumerable.Range(1, index).Select(value => new Guid(value, 0, 0, new byte[8])).Order());
                listed.Single(value => value.Id == id).Title.ShouldBe($"Session {index}");
                listed.Count(value => value.Archived).ShouldBe(index / 2);
            }
        }

        await Should.ThrowAsync<IOException>(() => store.ListAsync(Token));
        await store.RemoveAsync(new Guid(10001, 0, 0, new byte[8]), 1, Token);
        (await store.ListAsync(Token)).Count.ShouldBe(10000);
    }

    private static async Task DamageAsync(string path, string damage)
    {
        if (damage == "missing") { File.Delete(path); return; }
        if (damage == "oversized")
        {
            using var stream = File.OpenWrite(path);
            stream.SetLength(8L * 1024 * 1024 + 1);
            return;
        }
        if (damage is "truncated" or "null")
        {
            await File.WriteAllTextAsync(path, damage == "null" ? "null" : "{\"format\":1,", Token);
            return;
        }
        var envelope = JsonNode.Parse(await File.ReadAllTextAsync(path, Token))!.AsObject();
        switch (damage)
        {
            case "checksum": envelope["value"]!["title"] = "Tampered without digest"; break;
            case "missing-value":
                envelope["value"] = null;
                await File.WriteAllTextAsync(path, envelope.ToJsonString(JsonSerializerOptions.Web), Token);
                return;
            case "invalid-type":
                envelope["value"]!["revision"] = "not-a-number";
                await File.WriteAllTextAsync(path, envelope.ToJsonString(JsonSerializerOptions.Web), Token);
                return;
            case "format": envelope["format"] = 2; break;
            case "empty-id": envelope["value"]!["id"] = Guid.Empty; break;
            case "zero-revision": envelope["value"]!["revision"] = 0; break;
            case "foreign-id": envelope["value"]!["id"] = Guid.NewGuid(); break;
            case "wrong-revision": envelope["value"]!["revision"] = 2; break;
            default: throw new ArgumentOutOfRangeException(nameof(damage));
        }
        if (damage != "checksum")
        {
            var value = envelope["value"]!.Deserialize<ConversationCheckpoint>(JsonSerializerOptions.Web)!;
            envelope["digest"] = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, JsonSerializerOptions.Web)));
        }
        await File.WriteAllTextAsync(path, envelope.ToJsonString(JsonSerializerOptions.Web), Token);
    }

    private static Task WriteEnvelopeAsync(string path, ConversationCheckpoint checkpoint)
    {
        var digest = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(checkpoint, JsonSerializerOptions.Web)));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { format = 1, value = checkpoint, digest }, JsonSerializerOptions.Web);
        return File.WriteAllBytesAsync(path, bytes, Token);
    }

    private static async Task InvokeAsync(IConversationStore store, string operation, Guid id, CancellationToken token)
    {
        switch (operation)
        {
            case "list": await store.ListAsync(token); break;
            case "commit": await store.CommitAsync(Snapshot(id) with { Title = "Attempted change" }, 1, token); break;
            case "read": await store.ReadAsync(id, 1, token); break;
            case "revisions": await store.RevisionsAsync(id, token); break;
            case "remove": await store.RemoveAsync(id, 1, token); break;
            default: throw new ArgumentOutOfRangeException(nameof(operation));
        }
    }

    private static ConversationCheckpoint Snapshot(Guid id)
    {
        var createdAt = DateTimeOffset.Parse("2026-01-02T03:04:05Z");
        var archive = new AgentSessionArchive(1, "provider/model", createdAt,
            [new(1, "user", "question\n\"quoted\" \u03bb", createdAt), new(2, "assistant", "answer", createdAt)],
            JsonSerializer.SerializeToElement(new { context = "durable" }));
        return new(id, 0, archive, "Original", false, 17, 23, 4, 2, true);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}