using System.Collections.Generic;
using DotnetDiagnosticsMcp.Core.Investigation;
using FluentAssertions;
using Xunit;

namespace DotnetDiagnosticsMcp.Core.Tests;

public class InvestigationPlannerTests
{
    private readonly InvestigationPlanner _planner = new();

    [Fact]
    public void Plan_DefaultsToColdMode_WhenNoHypothesisOrBaseline()
    {
        var plan = _planner.Plan(new InvestigationRequest(ProcessId: 1234, Symptom: "high latency"));

        plan.Mode.Should().Be(InvestigationMode.Cold);
        plan.NextStep.ToolName.Should().Be("snapshot_counters", "cold investigations must start with vitals");
        plan.NextStep.StepNumber.Should().Be(1);
        plan.AllSteps.Should().HaveCountGreaterThan(1);
        plan.EarlyStopConditions.Should().Contain(e => e.ConditionId == "max-tool-calls-reached");
        plan.BaselineComparisons.Should().BeNull();
    }

    [Fact]
    public void Plan_PicksWarmMode_WhenBaselineProvided_AndEmitsComparisons()
    {
        var baseline = new BaselineHandle(
            InvestigationId: "inv-prev",
            SnapshotAt: DateTimeOffset.UtcNow.AddHours(-1),
            KeyMetrics: new Dictionary<string, double> { ["cpu_pct"] = 23.4, ["gen2_count"] = 0.5 });

        var plan = _planner.Plan(new InvestigationRequest(ProcessId: 1234, Baseline: baseline));

        plan.Mode.Should().Be(InvestigationMode.Warm);
        plan.NextStep.StepId.Should().Be("vitals-delta");
        plan.BaselineComparisons.Should().NotBeNull().And.HaveCount(2);
        plan.BaselineComparisons!.Select(c => c.MetricName).Should().BeEquivalentTo(new[] { "cpu_pct", "gen2_count" });
    }

    [Theory]
    [InlineData("lock contention on Cart.Checkout", "collect_event_source", "lock-events")]
    [InlineData("memory leak in payment service", "snapshot_counters", "memory-vitals")]
    [InlineData("threadpool starvation after release", "snapshot_counters", "tp-vitals")]
    [InlineData("exception storm from validation", "collect_exceptions", "exception-collect")]
    [InlineData("hot CPU on Regex matching", "snapshot_counters", "cpu-vitals")]
    [InlineData("cold start regression on startup", "snapshot_counters", "startup-vitals")]
    public void Plan_RoutesHypothesisByKeyword(string hypothesis, string expectedTool, string expectedStepId)
    {
        var plan = _planner.Plan(new InvestigationRequest(ProcessId: 1234, Hypothesis: hypothesis));

        plan.Mode.Should().Be(InvestigationMode.Hypothesis);
        plan.NextStep.ToolName.Should().Be(expectedTool);
        plan.NextStep.StepId.Should().Be(expectedStepId);
        plan.EarlyStopConditions.Should().Contain(e => e.ConditionId == "hypothesis-confirmed");
        plan.EarlyStopConditions.Should().Contain(e => e.ConditionId == "hypothesis-refuted");
    }

    [Fact]
    public void Plan_UnknownHypothesis_FallsBackToVitals()
    {
        var plan = _planner.Plan(new InvestigationRequest(ProcessId: 1234, Hypothesis: "weird mystery thing"));

        plan.Mode.Should().Be(InvestigationMode.Hypothesis);
        plan.NextStep.ToolName.Should().Be("snapshot_counters");
        plan.NextStep.StepId.Should().Be("vitals");
    }

    [Fact]
    public void Plan_HypothesisWinsOverBaseline_WhenBothProvided()
    {
        var baseline = new BaselineHandle("inv-prev", DateTimeOffset.UtcNow, new Dictionary<string, double>());
        var plan = _planner.Plan(new InvestigationRequest(1234, Hypothesis: "lock contention", Baseline: baseline));

        plan.Mode.Should().Be(InvestigationMode.Hypothesis);
    }

    [Fact]
    public void Plan_HonorsCustomConstraints()
    {
        var plan = _planner.Plan(new InvestigationRequest(
            1234,
            Symptom: "latency",
            Constraints: new InvestigationConstraints(MaxToolCalls: 3, DumpRequiresApproval: false, MaxDumpType: "Triage")));

        plan.Constraints.MaxToolCalls.Should().Be(3);
        plan.Constraints.DumpRequiresApproval.Should().BeFalse();
        plan.Constraints.MaxDumpType.Should().Be("Triage");
    }

    [Fact]
    public void Plan_RejectsInvalidProcessId()
    {
        var act = () => _planner.Plan(new InvestigationRequest(ProcessId: 0));
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Plan_AllBranchTargets_ResolveToStepOrTerminal_AcrossModes()
    {
        var baseline = new BaselineHandle("inv-base", DateTimeOffset.UtcNow, new Dictionary<string, double> { ["cpu_pct"] = 10 });
        var plans = new[]
        {
            _planner.Plan(new InvestigationRequest(1234, Symptom: "latency")),
            _planner.Plan(new InvestigationRequest(1234, Baseline: baseline)),
            _planner.Plan(new InvestigationRequest(1234, Hypothesis: "lock contention on x")),
            _planner.Plan(new InvestigationRequest(1234, Hypothesis: "memory leak")),
            _planner.Plan(new InvestigationRequest(1234, Hypothesis: "cpu hot path")),
            _planner.Plan(new InvestigationRequest(1234, Hypothesis: "weird mystery thing")),
        };

        foreach (var plan in plans)
        {
            var validTargets = new HashSet<string>(plan.AllSteps.Select(s => s.StepId)
                .Concat(plan.Terminals.Select(t => t.TerminalId)));
            foreach (var step in plan.AllSteps)
            {
                foreach (var branch in step.Branches)
                {
                    validTargets.Should().Contain(branch.NextStepId,
                        $"branch '{branch.Condition}' in step '{step.StepId}' (mode={plan.Mode}) must point to a real step or terminal");
                }
            }
        }
    }

    [Fact]
    public void Plan_LockEventsStep_EmitsContentionKeywordAsLong()
    {
        var plan = _planner.Plan(new InvestigationRequest(1234, Hypothesis: "lock contention on Foo"));
        var lockStep = plan.AllSteps.First(s => s.StepId == "lock-events");

        lockStep.ToolParams.Should().ContainKey("keywords");
        var kw = lockStep.ToolParams["keywords"];
        kw.Should().BeOfType<long>("collect_event_source.keywords is typed `long` — string would fail schema validation");
        ((long)kw!).Should().Be(0x4000L);
    }

    [Fact]
    public void Plan_DumpTerminal_IsAlwaysApprovalGated_RegardlessOfConstraints()
    {
        var plan = _planner.Plan(new InvestigationRequest(
            1234,
            Hypothesis: "memory leak in cache",
            Constraints: new InvestigationConstraints(DumpRequiresApproval: false)));

        var dump = plan.Terminals.First(t => t.TerminalId == "dump-heap");
        dump.RequiresApproval.Should().BeTrue(
            "dumps must remain approval-gated even when global flag is off — Mini still pauses production");
    }

    [Fact]
    public void Plan_AcceptsCustomIdFactory_ForDeterministicSnapshotTests()
    {
        var seq = 0;
        var planner = new InvestigationPlanner(idFactory: () => $"inv-test-{++seq}");

        var first = planner.Plan(new InvestigationRequest(1234, Symptom: "latency"));
        var second = planner.Plan(new InvestigationRequest(1234, Symptom: "latency"));

        first.InvestigationId.Should().Be("inv-test-1");
        second.InvestigationId.Should().Be("inv-test-2");
    }
}
