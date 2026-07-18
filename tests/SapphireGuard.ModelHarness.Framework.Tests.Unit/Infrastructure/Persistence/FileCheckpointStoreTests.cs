using System.Globalization;
using SapphireGuard.ModelHarness.Framework.Persistence;
using SapphireGuard.ModelHarness.Framework.State;
using SapphireGuard.ModelHarness.Infrastructure.Persistence;
using Xunit;
using StateBudget = SapphireGuard.ModelHarness.Framework.State.Budget;

namespace SapphireGuard.ModelHarness.Framework.Tests.Unit.Infrastructure.Persistence;

public sealed class FileCheckpointStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ckpttest_" + Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private static readonly StateBudget Budget = new()
    {
        MaxTurns = 10,
        MaxTotalTokens = 1000,
        MaxCost = 1m,
        MaxWallClock = TimeSpan.FromMinutes(1)
    };

    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static AgentState SampleState() => AgentState.NewTask("checkpoint me", Budget, T0);

    private static Checkpoint At(AgentState state, string id, DateTimeOffset createdAt, int turn) =>
        new() { CheckpointId = id, RunId = "run-1", CreatedAt = createdAt, TurnNumber = turn, State = state };

    [Fact]
    public async Task Load_SameCheckpointIdSavedTwice_ReturnsTheNewer()
    {
        // SaveAsync documents "overwrites any existing checkpoint with the same CheckpointId", but the
        // filename carries CreatedAt, so a re-save at a different time leaves two files. LoadAsync took
        // an arbitrary match (GetFiles has no ordering guarantee), which could resurrect the superseded
        // one — silently resuming from state the caller believed it had replaced.
        var store = new FileCheckpointStore(_dir);
        var state = SampleState();

        await store.SaveAsync(At(state, "same-id", T0, turn: 1));
        await store.SaveAsync(At(state, "same-id", T0.AddSeconds(5), turn: 2));

        var loaded = await store.LoadAsync("same-id");

        Assert.Equal(2, loaded!.TurnNumber);
    }

    [Fact]
    public async Task Load_CorruptNewestMatch_FallsBackToTheIntactOlderOne()
    {
        var store = new FileCheckpointStore(_dir);
        var state = SampleState();

        await store.SaveAsync(At(state, "same-id", T0, turn: 1));
        await store.SaveAsync(At(state, "same-id", T0.AddSeconds(5), turn: 2));

        var taskDir = Path.Combine(_dir, state.TaskId);
        var newest = Directory.GetFiles(taskDir, "*_same-id.json").OrderDescending().First();
        await File.WriteAllTextAsync(newest, "{ this is not valid json");

        var loaded = await store.LoadAsync("same-id");

        Assert.Equal(1, loaded!.TurnNumber);
    }

    [Fact]
    public async Task Load_EveryMatchCorrupt_ReturnsNullRatherThanThrowing()
    {
        // The by-ID sibling was the unhardened one: it read its match directly, so a torn file threw
        // where the interface documents null. It now skips like LoadLatestAsync does. Corrupting *every*
        // match is what makes this a real guard — with only the newest corrupted the old code passed for
        // the wrong reason, because its arbitrary `matches[0]` happened to be the intact older file.
        var store = new FileCheckpointStore(_dir);
        var state = SampleState();

        await store.SaveAsync(At(state, "same-id", T0, turn: 1));

        var taskDir = Path.Combine(_dir, state.TaskId);
        foreach (var file in Directory.GetFiles(taskDir, "*_same-id.json"))
            await File.WriteAllTextAsync(file, "{ this is not valid json");

        Assert.Null(await store.LoadAsync("same-id"));
    }

    [Theory]
    [InlineData("*")]
    [InlineData("a?c")]
    [InlineData("../escape")]
    public async Task Load_CheckpointIdWithWildcardOrSeparator_Throws(string checkpointId)
    {
        // The ID is interpolated into a search glob, so a wildcard would widen the match to unrelated
        // checkpoints and a separator would reach outside the task directory.
        var store = new FileCheckpointStore(_dir);

        await Assert.ThrowsAsync<ArgumentException>(() => store.LoadAsync(checkpointId));
    }

    [Fact]
    public async Task LoadLatest_CheckpointsWrittenUnderDifferentCalendarCultures_StillReturnsTheNewest()
    {
        // The filename's timestamp prefix is the whole sort key, so it must not follow the ambient
        // culture: a Buddhist-calendar locale renders 2026 as 2569, which sorts above every Gregorian
        // name regardless of true recency. Write the OLDER checkpoint under th-TH and the newer under
        // en-US — what a container rebuilt with LANG=th_TH.UTF-8 produces — and the stale one wins the
        // resume, replaying or losing turns.
        var thai = new CultureInfo("th-TH");
        Assert.IsType<ThaiBuddhistCalendar>(thai.DateTimeFormat.Calendar); // the case is only live with ICU present

        var store = new FileCheckpointStore(_dir);
        var state = SampleState();

        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = thai;
            await store.SaveAsync(At(state, "older", T0, turn: 1));
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            await store.SaveAsync(At(state, "newer", T0.AddSeconds(1), turn: 2));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }

        var loaded = await store.LoadLatestAsync(state.TaskId);

        Assert.Equal("newer", loaded!.CheckpointId);
    }

    [Fact]
    public async Task Save_ThenLoadLatest_RoundTripsTheState()
    {
        var store = new FileCheckpointStore(_dir);
        var state = SampleState();

        await store.SaveAsync(At(state, "c1", T0, turn: 0));
        var loaded = await store.LoadLatestAsync(state.TaskId);

        Assert.NotNull(loaded);
        Assert.Equal("c1", loaded!.CheckpointId);
        Assert.Equal(state.TaskId, loaded.State.TaskId);
        Assert.Equal(state.TaskText, loaded.State.TaskText);
    }

    [Fact]
    public async Task LoadLatest_ReturnsNewestByTimestampOrderedFilename()
    {
        var store = new FileCheckpointStore(_dir);
        var state = SampleState();

        // Same task, two turns — the store relies on the timestamp filename prefix sorting
        // lexicographically == chronologically. Save older first to prove it isn't just "last written".
        await store.SaveAsync(At(state, "older", T0, turn: 1));
        await store.SaveAsync(At(state, "newer", T0.AddSeconds(1), turn: 2));

        var loaded = await store.LoadLatestAsync(state.TaskId);

        Assert.Equal("newer", loaded!.CheckpointId);
        Assert.Equal(2, loaded.TurnNumber);
    }

    [Fact]
    public async Task LoadById_FindsCheckpointAcrossTaskDirectories()
    {
        var store = new FileCheckpointStore(_dir);
        var a = SampleState();
        var b = SampleState(); // distinct TaskId → distinct directory

        await store.SaveAsync(At(a, "ca", T0, turn: 0));
        await store.SaveAsync(At(b, "cb", T0, turn: 0));

        var loaded = await store.LoadAsync("cb");

        Assert.NotNull(loaded);
        Assert.Equal(b.TaskId, loaded!.State.TaskId);
    }

    [Fact]
    public async Task LoadById_UnknownId_ReturnsNull()
    {
        var store = new FileCheckpointStore(_dir);
        await store.SaveAsync(At(SampleState(), "c1", T0, turn: 0));

        Assert.Null(await store.LoadAsync("does-not-exist"));
    }

    [Fact]
    public async Task LoadLatest_UnknownTask_ReturnsNull()
    {
        var store = new FileCheckpointStore(_dir);

        Assert.Null(await store.LoadLatestAsync("no-such-task"));
    }

    [Fact]
    public async Task Save_ThenLoadLatest_HonoursCallerSuppliedTaskId()
    {
        var store = new FileCheckpointStore(_dir);
        var state = AgentState.NewTask("checkpoint me", Budget, T0, taskId: "job-12345");

        await store.SaveAsync(At(state, "c1", T0, turn: 0));
        var loaded = await store.LoadLatestAsync("job-12345");

        Assert.NotNull(loaded);
        Assert.Equal("job-12345", loaded!.State.TaskId);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("nested/segment")]
    [InlineData("..")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Save_RejectsUnsafeTaskId(string taskId)
    {
        var store = new FileCheckpointStore(_dir);
        var state = AgentState.NewTask("x", Budget, T0, taskId: taskId);

        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(At(state, "c1", T0, turn: 0)));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("nested/segment")]
    [InlineData("..")]
    [InlineData("")]
    public async Task LoadLatest_RejectsUnsafeTaskId(string taskId)
    {
        var store = new FileCheckpointStore(_dir);

        await Assert.ThrowsAsync<ArgumentException>(() => store.LoadLatestAsync(taskId));
    }

    [Fact]
    public async Task LoadLatest_SkipsCorruptNewest_FallsBackToPriorIntact()
    {
        var store = new FileCheckpointStore(_dir);
        var state = SampleState();

        await store.SaveAsync(At(state, "older", T0, turn: 1));
        await store.SaveAsync(At(state, "newer", T0.AddSeconds(1), turn: 2));

        // Simulate a crash mid-write: the newest file is left as incomplete/garbage JSON.
        var taskDir = Path.Combine(_dir, state.TaskId);
        var newest = Directory.GetFiles(taskDir, "*.json").OrderDescending().First();
        await File.WriteAllTextAsync(newest, "{ this is not valid json");

        var loaded = await store.LoadLatestAsync(state.TaskId);

        Assert.NotNull(loaded);
        Assert.Equal("older", loaded!.CheckpointId);
        Assert.Equal(1, loaded.TurnNumber);
    }

    [Fact]
    public async Task Save_LeavesNoTempFilesBehind()
    {
        var store = new FileCheckpointStore(_dir);
        var state = SampleState();

        await store.SaveAsync(At(state, "c1", T0, turn: 0));

        var taskDir = Path.Combine(_dir, state.TaskId);
        Assert.Empty(Directory.GetFiles(taskDir, "*.tmp"));
        Assert.Single(Directory.GetFiles(taskDir, "*.json"));
    }

    [Fact]
    public async Task Delete_RemovesAllCheckpointsForTask()
    {
        var store = new FileCheckpointStore(_dir);
        var state = SampleState();
        await store.SaveAsync(At(state, "c1", T0, turn: 0));
        await store.SaveAsync(At(state, "c2", T0.AddSeconds(1), turn: 1));

        await store.DeleteAsync(state.TaskId);

        Assert.Null(await store.LoadLatestAsync(state.TaskId));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("nested/segment")]
    [InlineData("..")]
    public async Task Delete_RejectsUnsafeTaskId(string taskId)
    {
        var store = new FileCheckpointStore(_dir);

        await Assert.ThrowsAsync<ArgumentException>(() => store.DeleteAsync(taskId));
    }
}
