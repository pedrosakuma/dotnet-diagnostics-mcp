namespace DotnetDiagnosticsMcp.Core.CpuSampling;

/// <summary>
/// Builds a merged caller→callee tree from sampled stacks. Used by both the managed
/// (<see cref="EventPipeCpuSampler"/>) and native (perf-based) samplers so the on-the-wire
/// shape of <see cref="CallTreeNode"/> is identical regardless of capture source.
/// </summary>
/// <remarks>
/// The aggregation key is composed by the caller — typically <c>module + "!" + display</c>
/// so two methods with the same FQN in different modules don't collide. <c>Display</c> is
/// the human-readable name surfaced in the tree node.
/// </remarks>
internal sealed class CallTreeBuilder
{
    private readonly Node _root = new(new SampledFrame(string.Empty, "<root>"));

    public void AddStack(List<(string Key, string Module, string Display)> rootToLeaf, string leafKey)
    {
        var current = _root;
        current.Inclusive++;
        for (var i = 0; i < rootToLeaf.Count; i++)
        {
            var (key, module, display) = rootToLeaf[i];
            if (!current.Children.TryGetValue(key, out var child))
            {
                child = new Node(new SampledFrame(module, display));
                current.Children[key] = child;
            }
            child.Inclusive++;
            if (key == leafKey && i == rootToLeaf.Count - 1)
            {
                child.Exclusive++;
            }
            current = child;
        }
    }

    public CallTreeNode Build() => Materialize(_root);

    private static CallTreeNode Materialize(Node n)
    {
        var children = n.Children.Values
            .OrderByDescending(c => c.Inclusive)
            .Select(Materialize)
            .ToList();
        return new CallTreeNode(n.Frame, n.Inclusive, n.Exclusive, children);
    }

    private sealed class Node
    {
        public Node(SampledFrame frame) { Frame = frame; }
        public SampledFrame Frame { get; }
        public long Inclusive;
        public long Exclusive;
        public Dictionary<string, Node> Children { get; } = new(StringComparer.Ordinal);
    }
}
