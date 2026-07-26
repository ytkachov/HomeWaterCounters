using WaterCounters.Core.Storage;

namespace WaterCounters.Core.Tests;

public class InMemoryRemoteStoreTests
{
    private static readonly byte[] Payload = "содержимое"u8.ToArray();

    [Fact]
    public async Task Upload_FailIfExists_RejectsSecondWrite()
    {
        var store = new InMemoryRemoteStore();
        await store.UploadAsync("/queue/a.json", Payload);

        await Assert.ThrowsAsync<RemoteConflictException>(
            () => store.UploadAsync("/queue/a.json", Payload));
    }

    [Fact]
    public async Task Upload_Overwrite_ReplacesContent()
    {
        var store = new InMemoryRemoteStore();
        await store.UploadAsync("/queue/a.json", Payload);
        await store.UploadAsync("/queue/a.json", "новое"u8.ToArray(), RemoteWriteMode.Overwrite);

        byte[] content = await store.DownloadAsync("/queue/a.json");
        Assert.Equal("новое", System.Text.Encoding.UTF8.GetString(content));
    }

    [Fact]
    public async Task Download_MissingPath_Throws()
    {
        var store = new InMemoryRemoteStore();
        await Assert.ThrowsAsync<RemoteNotFoundException>(() => store.DownloadAsync("/nope.json"));
    }

    [Fact]
    public async Task List_ReturnsOnlyDirectChildren()
    {
        var store = new InMemoryRemoteStore();
        await store.UploadAsync("/queue/a.json", Payload);
        await store.UploadAsync("/queue/b.json", Payload);
        await store.UploadAsync("/queue/nested/c.json", Payload);

        IReadOnlyList<RemoteEntry> entries = await store.ListAsync("/queue");

        Assert.Equal(["/queue/a.json", "/queue/b.json"], entries.Select(e => e.Path));
    }

    [Fact]
    public async Task Move_TransfersContentAndFreesSource()
    {
        var store = new InMemoryRemoteStore();
        await store.UploadAsync("/from/a.json", Payload);

        RemoteEntry moved = await store.MoveAsync("/from/a.json", "/to/a.json");

        Assert.Equal("/to/a.json", moved.Path);
        Assert.False(await store.ExistsAsync("/from/a.json"));
        Assert.Equal(Payload, await store.DownloadAsync("/to/a.json"));
    }

    [Fact]
    public async Task Move_ToOccupiedPath_Throws()
    {
        var store = new InMemoryRemoteStore();
        await store.UploadAsync("/from/a.json", Payload);
        await store.UploadAsync("/to/a.json", Payload);

        await Assert.ThrowsAsync<RemoteConflictException>(() => store.MoveAsync("/from/a.json", "/to/a.json"));
    }

    [Fact]
    public async Task Move_IsAtomicUnderConcurrency()
    {
        // Ровно эта гарантия делает возможным «захват» задачи из очереди:
        // сколько бы обработчиков ни стартовало, задачу получит один.
        var store = new InMemoryRemoteStore();
        await store.UploadAsync("/pending/task.json", Payload);

        var barrier = new TaskCompletionSource();
        store.BeforeOperation = async (_, _) => await barrier.Task;

        Task<RemoteEntry>[] racers =
        [
            .. Enumerable.Range(0, 8).Select(i =>
                store.MoveAsync("/pending/task.json", $"/processing/task-{i}.json"))
        ];

        barrier.SetResult();

        RemoteEntry[] winners = [];
        int conflicts = 0;

        foreach (Task<RemoteEntry> racer in racers)
        {
            try
            {
                winners = [.. winners, await racer];
            }
            catch (RemoteNotFoundException)
            {
                conflicts++;
            }
        }

        Assert.Single(winners);
        Assert.Equal(7, conflicts);
    }

    [Fact]
    public async Task WaitForChanges_ReturnsImmediatelyWhenChangesAlreadyPending()
    {
        var store = new InMemoryRemoteStore();
        string cursor = await store.GetCursorAsync("/queue");

        await store.UploadAsync("/queue/a.json", Payload);

        RemoteChanges changes = await store.WaitForChangesAsync(cursor, TimeSpan.FromSeconds(30));

        Assert.True(changes.HasChanges);
        Assert.Equal(["/queue/a.json"], changes.ChangedPaths);
    }

    [Fact]
    public async Task WaitForChanges_WakesUpOnLaterWrite()
    {
        var store = new InMemoryRemoteStore();
        string cursor = await store.GetCursorAsync("/queue");

        Task<RemoteChanges> waiting = store.WaitForChangesAsync(cursor, TimeSpan.FromSeconds(30));
        Assert.False(waiting.IsCompleted);

        await store.UploadAsync("/queue/a.json", Payload);

        RemoteChanges changes = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(["/queue/a.json"], changes.ChangedPaths);
    }

    [Fact]
    public async Task WaitForChanges_IgnoresWritesOutsideWatchedFolder()
    {
        var store = new InMemoryRemoteStore();
        string cursor = await store.GetCursorAsync("/queue/to-desktop");

        await store.UploadAsync("/queue/to-mobile/other.json", Payload);

        RemoteChanges changes = await store.WaitForChangesAsync(cursor, TimeSpan.FromMilliseconds(50));

        Assert.False(changes.HasChanges);
    }

    [Fact]
    public async Task WaitForChanges_ReportsDeletions()
    {
        var store = new InMemoryRemoteStore();
        await store.UploadAsync("/queue/a.json", Payload);
        string cursor = await store.GetCursorAsync("/queue");

        await store.DeleteAsync("/queue/a.json");

        RemoteChanges changes = await store.WaitForChangesAsync(cursor, TimeSpan.FromSeconds(5));

        Assert.Equal(["/queue/a.json"], changes.DeletedPaths);
        Assert.Empty(changes.ChangedPaths);
    }

    [Theory]
    [InlineData("queue/a.json", "/queue/a.json")]
    [InlineData("\\queue\\a.json", "/queue/a.json")]
    [InlineData("/queue/a.json/", "/queue/a.json")]
    [InlineData("  /queue/a.json  ", "/queue/a.json")]
    public void RemotePath_Normalize(string input, string expected)
    {
        Assert.Equal(expected, RemotePath.Normalize(input));
    }

    [Theory]
    [InlineData("/queue//a.json")]
    [InlineData("/queue/../secrets")]
    public void RemotePath_RejectsSuspiciousPaths(string input)
    {
        Assert.Throws<ArgumentException>(() => RemotePath.Normalize(input));
    }

    [Fact]
    public void RemotePath_Helpers()
    {
        Assert.Equal("/a/b/c.json", RemotePath.Combine("/a", "b", "c.json"));
        Assert.Equal("c.json", RemotePath.GetFileName("/a/b/c.json"));
        Assert.Equal("/a/b", RemotePath.GetFolder("/a/b/c.json"));
        Assert.True(RemotePath.IsInFolder("/a/b/c.json", "/a"));
        Assert.False(RemotePath.IsInFolder("/ab/c.json", "/a"));
    }
}
