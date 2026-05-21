using System.Globalization;

namespace DotnetDiagnosticsMcp.Core.Threads;

/// <summary>
/// Pure in-memory deadlock detection over a captured <see cref="ThreadSnapshotArtifact"/>. Uses a
/// DFS over waiter→owner edges inferred from the lock graph and returns every unique simple cycle
/// up to the requested cap.
/// </summary>
public static class ThreadDeadlockDetector
{
    public static IReadOnlyList<ThreadDeadlockCycle> Detect(ThreadSnapshotArtifact snapshot, string handle, int maxCycles)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrEmpty(handle);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCycles);

        var threadsById = snapshot.Threads
            .Where(t => t.ManagedThreadId > 0)
            .ToDictionary(t => t.ManagedThreadId);

        var edges = snapshot.Locks
            .Where(l => l.OwnerManagedThreadId > 0 && l.WaitingManagedThreadIds.Count > 0)
            .SelectMany(l => l.WaitingManagedThreadIds
                .Where(waiterId => waiterId > 0 && waiterId != l.OwnerManagedThreadId)
                .Where(waiterId => threadsById.ContainsKey(waiterId) && threadsById.ContainsKey(l.OwnerManagedThreadId))
                .Distinct()
                .Select(waiterId => new WaitForEdge(waiterId, l.OwnerManagedThreadId, l.ObjectAddress, l.ObjectTypeFullName, l.LockKind)))
            .Distinct()
            .ToArray();

        if (edges.Length == 0)
        {
            return Array.Empty<ThreadDeadlockCycle>();
        }

        var edgesByWaiter = edges
            .GroupBy(edge => edge.WaitingThreadId)
            .ToDictionary(group => group.Key, group => group.OrderBy(edge => edge.OwnerThreadId).ThenBy(edge => edge.LockObjectAddress).ToArray());

        var cycles = new List<ThreadDeadlockCycle>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var startThreadId in edgesByWaiter.Keys.OrderBy(id => id))
        {
            var visited = new HashSet<int> { startThreadId };
            var path = new List<WaitForEdge>();
            Explore(startThreadId, startThreadId, visited, path);
            if (cycles.Count >= maxCycles)
            {
                break;
            }
        }

        return cycles;

        void Explore(int startThreadId, int currentThreadId, HashSet<int> visited, List<WaitForEdge> path)
        {
            if (cycles.Count >= maxCycles || !edgesByWaiter.TryGetValue(currentThreadId, out var outgoing))
            {
                return;
            }

            foreach (var edge in outgoing)
            {
                if (cycles.Count >= maxCycles)
                {
                    return;
                }

                if (edge.OwnerThreadId == startThreadId)
                {
                    if (path.Count == 0)
                    {
                        continue;
                    }

                    path.Add(edge);
                    var key = BuildCanonicalKey(path);
                    if (seen.Add(key))
                    {
                        cycles.Add(BuildCycle(path, threadsById, handle));
                    }
                    path.RemoveAt(path.Count - 1);
                    continue;
                }

                if (!visited.Add(edge.OwnerThreadId))
                {
                    continue;
                }

                path.Add(edge);
                Explore(startThreadId, edge.OwnerThreadId, visited, path);
                path.RemoveAt(path.Count - 1);
                visited.Remove(edge.OwnerThreadId);
            }
        }
    }

    private static ThreadDeadlockCycle BuildCycle(
        IReadOnlyList<WaitForEdge> cycleEdges,
        Dictionary<int, ManagedThread> threadsById,
        string handle)
    {
        var members = cycleEdges
            .Select(edge => threadsById[edge.WaitingThreadId])
            .Select(thread => new ThreadDeadlockMember(
                ThreadId: thread.ManagedThreadId,
                OSThreadId: thread.OSThreadId,
                State: thread.State,
                TopFrameMethod: thread.TopFrameMethod,
                InferredWaitReason: thread.InferredWaitReason))
            .ToArray();

        var lockChain = cycleEdges
            .Select(edge => new ThreadDeadlockLink(
                WaitingThreadId: edge.WaitingThreadId,
                OwnerThreadId: edge.OwnerThreadId,
                LockObjectAddress: edge.LockObjectAddress,
                LockObjectTypeFullName: edge.LockObjectTypeFullName,
                LockKind: edge.LockKind))
            .ToArray();

        var commands = new List<ThreadDeadlockCommand>
        {
            new("!threads", "List managed/runtime threads and map managed ids to OS threads."),
            new("!syncblk", "Inspect monitor owners, recursion counts, and waiter counts for the contended locks."),
        };

        foreach (var member in members)
        {
            commands.Add(new ThreadDeadlockCommand(
                $"~~[{member.OSThreadId.ToString("x", CultureInfo.InvariantCulture)}]s; !clrstack",
                $"Inspect the blocked stack for managed thread {member.ThreadId} after mapping ids with !threads. MCP equivalent: query_thread_snapshot(handle=\"{handle}\", view=\"stack\", threadId={member.ThreadId})."));
        }

        return new ThreadDeadlockCycle(members, lockChain, commands);
    }

    private static string BuildCanonicalKey(IReadOnlyList<WaitForEdge> cycleEdges)
    {
        var representations = new List<string>(cycleEdges.Count);
        for (var rotation = 0; rotation < cycleEdges.Count; rotation++)
        {
            var parts = new string[cycleEdges.Count];
            for (var index = 0; index < cycleEdges.Count; index++)
            {
                var edge = cycleEdges[(rotation + index) % cycleEdges.Count];
                parts[index] = string.Create(
                    CultureInfo.InvariantCulture,
                    $"{edge.WaitingThreadId}->{edge.OwnerThreadId}@{edge.LockObjectAddress:x}");
            }

            representations.Add(string.Join("|", parts));
        }

        representations.Sort(StringComparer.Ordinal);
        return representations[0];
    }

    private sealed record WaitForEdge(
        int WaitingThreadId,
        int OwnerThreadId,
        ulong LockObjectAddress,
        string? LockObjectTypeFullName,
        string LockKind);
}
