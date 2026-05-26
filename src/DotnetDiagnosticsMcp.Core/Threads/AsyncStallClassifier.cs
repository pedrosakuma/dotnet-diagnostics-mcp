namespace DotnetDiagnosticsMcp.Core.Threads;

/// <summary>
/// Best-effort classifier for async-looking thread stacks captured by <c>collect_thread_snapshot</c>.
/// This stays purely consumer-side: it inspects an existing <see cref="ThreadSnapshotArtifact"/> and
/// groups threads into a few high-signal async stall buckets so callers do not need to eyeball every
/// stack manually.
/// </summary>
public static class AsyncStallClassifier
{
    private const string SyncOverAsync = "SyncOverAsync";
    private const string ChannelAwait = "ChannelAwait";
    private const string TcsPending = "TcsPending";
    private const string SemaphoreAwait = "SemaphoreAwait";
    private const string Delay = "Delay";
    private const string Unknown = "Unknown";

    private static readonly string[] BucketOrder = [SyncOverAsync, ChannelAwait, TcsPending, SemaphoreAwait, Delay, Unknown];

    public static AsyncStallsView Classify(ThreadSnapshotArtifact snapshot, int topN)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(topN);

        var classified = snapshot.Threads
            .Select(thread => (Thread: thread, Bucket: ClassifyThread(thread)))
            .Where(entry => entry.Bucket is not null)
            .Select(entry => new ClassifiedThread(entry.Thread, entry.Bucket!))
            .ToArray();

        var byBucket = BucketOrder
            .Select(bucket => new
            {
                Bucket = bucket,
                Threads = classified.Where(entry => string.Equals(entry.Bucket, bucket, StringComparison.Ordinal)).ToArray(),
            })
            .Where(group => group.Threads.Length > 0)
            .Select(group => new AsyncStallBucketSummary(
                group.Bucket,
                group.Threads.Length,
                group.Threads.Select(entry => entry.Thread.ManagedThreadId).Distinct().Take(5).ToArray()))
            .ToArray();

        var topBlocked = classified
            .OrderByDescending(entry => entry.Thread.IsLikelyBlocked)
            .ThenBy(entry => Array.IndexOf(BucketOrder, entry.Bucket))
            .ThenBy(entry => entry.Thread.ManagedThreadId)
            .Take(topN)
            .Select(entry => new AsyncStalledThread(
                entry.Thread.ManagedThreadId,
                entry.Bucket,
                null,
                GetTopFrames(entry.Thread)))
            .ToArray();

        return new AsyncStallsView(
            View: "async-stalls",
            ClassifiedThreads: classified.Length,
            ByBucket: byBucket,
            TopBlockedAsync: topBlocked);
    }

    private static string? ClassifyThread(ManagedThread thread)
    {
        ArgumentNullException.ThrowIfNull(thread);

        var top = thread.TopFrameMethod ?? (thread.Frames.Count > 0 ? thread.Frames[0].DisplayName : null) ?? string.Empty;
        if (HasFrame(thread, frame =>
                Contains(frame, "System.Threading.Channels")
                && (Contains(frame, "UnboundedChannelReader")
                    || Contains(frame, "WaitToReadAsync")
                    || Contains(frame, "ReadAsync"))))
        {
            return ChannelAwait;
        }

        var hasTcsHolder = HasFrame(thread, frame => Contains(frame, "TaskCompletionSource"));
        var hasTcsTaskGetter = HasFrame(thread, frame =>
            Contains(frame, "TaskCompletionSource") && (Contains(frame, "get_Task") || Contains(frame, ".Task")));
        var hasAsyncBuilderFrame = HasFrame(thread, frame => Contains(frame, "AwaitUnsafeOnCompleted"))
            || HasFrame(thread, LooksAsyncish);
        if (hasTcsTaskGetter || (hasTcsHolder && hasAsyncBuilderFrame))
        {
            return TcsPending;
        }

        if (IsSyncOverAsync(top))
        {
            return SyncOverAsync;
        }

        if (HasFrame(thread, frame => Contains(frame, "SemaphoreSlim") && Contains(frame, "WaitAsync")))
        {
            return SemaphoreAwait;
        }

        if (HasFrame(thread, frame => Contains(frame, "Task.Delay") || Contains(frame, "DelayPromise")))
        {
            return Delay;
        }

        if (HasFrame(thread, LooksAsyncish))
        {
            return Unknown;
        }

        return null;
    }

    private static bool IsSyncOverAsync(string topFrame)
        => (Contains(topFrame, "Task") && (Contains(topFrame, "get_Result") || Contains(topFrame, ".Result")))
            || Contains(topFrame, "Task.Wait")
            || Contains(topFrame, "TaskAwaiter.GetResult")
            || Contains(topFrame, "ValueTaskAwaiter.GetResult");

    private static bool LooksAsyncish(string frame)
        => Contains(frame, "AsyncStateMachine")
            || Contains(frame, "AwaitUnsafeOnCompleted")
            || (Contains(frame, "MoveNext")
                && (Contains(frame, "d__")
                    || Contains(frame, "<")
                    || Contains(frame, "Async")
                    || Contains(frame, "IAsyncStateMachine")));

    private static bool HasFrame(ManagedThread thread, Func<string, bool> predicate)
        => GetFrameTexts(thread).Any(predicate);

    private static string[] GetFrameTexts(ManagedThread thread)
    {
        if (thread.Frames.Count == 0)
        {
            return string.IsNullOrWhiteSpace(thread.TopFrameMethod) ? [] : [thread.TopFrameMethod];
        }

        return thread.Frames
            .Select(frame => string.IsNullOrWhiteSpace(frame.TypeFullName)
                ? frame.DisplayName
                : $"{frame.DisplayName} {frame.TypeFullName}")
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .ToArray();
    }

    private static string[] GetTopFrames(ManagedThread thread)
    {
        var frames = thread.Frames
            .Select(frame => frame.DisplayName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Take(5)
            .ToArray();

        if (frames.Length > 0)
        {
            return frames;
        }

        return string.IsNullOrWhiteSpace(thread.TopFrameMethod) ? [] : [thread.TopFrameMethod];
    }

    private static bool Contains(string text, string value)
        => text.Contains(value, StringComparison.OrdinalIgnoreCase);

    private sealed record ClassifiedThread(ManagedThread Thread, string Bucket);
}
