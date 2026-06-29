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
        MaxContextTokens = 1000,
        MaxCost = 1m,
        MaxWallClock = TimeSpan.FromMinutes(1)
    };

    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static AgentState SampleState() => AgentState.NewTask("checkpoint me", Budget, T0);

    private static Checkpoint At(AgentState state, string id, DateTimeOffset createdAt, int turn) =>
        new() { CheckpointId = id, RunId = "run-1", CreatedAt = createdAt, TurnNumber = turn, State = state };

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
}
