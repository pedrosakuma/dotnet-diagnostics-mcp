using DotnetDiagnosticsMcp.Core.CpuSampling;
using DotnetDiagnosticsMcp.Core.Memory;
using FluentAssertions;
using Xunit;

namespace DotnetDiagnosticsMcp.Core.Tests;

/// <summary>
/// Wiring tests for the handoff contract (issue #18 / dotnet-assembly-mcp).
/// Each hotspot returned by collect_cpu_sample must carry an optional
/// <see cref="MethodIdentity"/> so the LLM can pass it verbatim to the
/// assembly-inspector MCP (<c>get_method</c>, <c>decompile_method</c>) without
/// any string parsing on either side.
/// </summary>
public class MethodIdentityHandoffTests
{
    private static readonly DateTimeOffset T0 = new(2026, 5, 19, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void MethodIdentity_HasCanonicalShape()
    {
        var id = new MethodIdentity(
            ModuleName: "MyApp.dll",
            ModulePath: "/app/MyApp.dll",
            ModuleVersionId: Guid.Parse("11111111-1111-1111-1111-111111111111"),
            MetadataToken: 0x06000028,
            TypeFullName: "MyApp.Services.OrderService",
            MethodName: "Process",
            GenericArity: 0);

        // The two fields the companion MCP actually consumes:
        id.ModuleVersionId.Should().NotBeNull();
        id.MetadataToken.Should().Be(0x06000028);
    }

    [Fact]
    public void Exporter_PropagatesIdentity_FromArtifactToHotspotSummary()
    {
        var hot = new SampledFrame("MyApp.dll", "MyApp.Services.OrderService.Process");
        var children = new[]
        {
            new CallTreeNode(hot, 100, 80, Array.Empty<CallTreeNode>()),
        };
        var root = new CallTreeNode(new SampledFrame(string.Empty, "<root>"), 1000, 0, children);

        var identity = new MethodIdentity(
            ModuleName: "MyApp.dll",
            ModulePath: "/app/MyApp.dll",
            ModuleVersionId: Guid.Parse("22222222-2222-2222-2222-222222222222"),
            MetadataToken: 0x06000099,
            TypeFullName: "MyApp.Services.OrderService",
            MethodName: "Process",
            GenericArity: 0);

        var ids = new Dictionary<SymbolRef, MethodIdentity>
        {
            [new SymbolRef("MyApp.dll", "MyApp.Services.OrderService.Process")] = identity,
        };

        var artifact = new CpuSampleTraceArtifact(
            1234, T0, TimeSpan.FromSeconds(5), 1000, root,
            ResolvedSources: null,
            MethodIdentities: ids);

        var exporter = new InvestigationSummaryExporter(
            new FixedProv(),
            clock: new FixedClk(T0),
            idFactory: () => "inv-id-1");

        var exported = exporter.Export(new ExportRequest("h-1", artifact));

        var hotspot = exported.Summary.Findings.TopHotspots.Single();
        hotspot.Identity.Should().NotBeNull();
        hotspot.Identity!.ModuleVersionId.Should().Be(identity.ModuleVersionId);
        hotspot.Identity.MetadataToken.Should().Be(0x06000099);
        hotspot.Identity.MethodName.Should().Be("Process");
    }

    [Fact]
    public void Artifact_DefaultsMethodIdentities_ToEmpty()
    {
        var root = new CallTreeNode(new SampledFrame(string.Empty, "<root>"), 0, 0, Array.Empty<CallTreeNode>());
        var artifact = new CpuSampleTraceArtifact(1, T0, TimeSpan.FromSeconds(1), 0, root);
        artifact.MethodIdentities.Should().NotBeNull();
        artifact.MethodIdentities.Should().BeEmpty();
    }

    [Fact]
    public void CallTreeIdentityProjector_StampsIdentityOntoMatchingFrame()
    {
        var child = new CallTreeNode(
            new SampledFrame("MyApp.dll", "MyApp.Services.OrderService.Process"),
            10,
            4,
            Array.Empty<CallTreeNode>());
        var root = new CallTreeNode(new SampledFrame(string.Empty, "<root>"), 10, 0, new[] { child });
        var identity = new MethodIdentity(
            ModuleName: "MyApp.dll",
            ModulePath: "/app/MyApp.dll",
            ModuleVersionId: Guid.Parse("33333333-3333-3333-3333-333333333333"),
            MetadataToken: 0x06000123,
            TypeFullName: "MyApp.Services.OrderService",
            MethodName: "Process",
            GenericArity: 0);

        var stamped = CallTreeIdentityProjector.Stamp(
            root,
            new Dictionary<SymbolRef, MethodIdentity>
            {
                [new SymbolRef("MyApp.dll", "MyApp.Services.OrderService.Process")] = identity,
            });

        stamped.Children.Single().Identity.Should().Be(identity);
        stamped.Identity.Should().BeNull();
    }

    private sealed class FixedClk : TimeProvider
    {
        private readonly DateTimeOffset _n;
        public FixedClk(DateTimeOffset n) { _n = n; }
        public override DateTimeOffset GetUtcNow() => _n;
    }

    [Fact]
    public void Parser_NonGeneric_ReturnsOpenTriple()
    {
        var p = EventPipeCpuSampler.ParseFullMethodName("MyApp.OrderService.Process");
        p.TypeFullName.Should().Be("MyApp.OrderService");
        p.MethodName.Should().Be("Process");
        p.GenericArity.Should().Be(0);
        p.GenericTypeArguments.Should().BeNull("non-generic methods must NOT carry an instantiation block");
    }

    [Fact]
    public void Parser_MethodLevelGeneric_ExtractsAngleBracketArgs()
    {
        var p = EventPipeCpuSampler.ParseFullMethodName("MyApp.Helper.Echo<System.Int32>");
        p.TypeFullName.Should().Be("MyApp.Helper");
        p.MethodName.Should().Be("Echo");
        p.GenericArity.Should().Be(1);
        p.GenericTypeArguments.Should().NotBeNull();
        p.GenericTypeArguments!.Type.Should().BeEmpty();
        p.GenericTypeArguments.Method.Should().Equal("System.Int32");
    }

    [Fact]
    public void Parser_TypeLevelGeneric_ExtractsBackticBracketArgs()
    {
        var p = EventPipeCpuSampler.ParseFullMethodName("System.Collections.Generic.List`1[System.Int32].Add");
        p.TypeFullName.Should().Be("System.Collections.Generic.List`1");
        p.MethodName.Should().Be("Add");
        p.GenericArity.Should().Be(0);
        p.GenericTypeArguments.Should().NotBeNull();
        p.GenericTypeArguments!.Type.Should().Equal("System.Int32");
        p.GenericTypeArguments.Method.Should().BeEmpty();
    }

    [Fact]
    public void Parser_TypeLevelGeneric_MultipleArgs()
    {
        var p = EventPipeCpuSampler.ParseFullMethodName("System.Collections.Generic.Dictionary`2[System.Int32,System.String].TryGetValue");
        p.TypeFullName.Should().Be("System.Collections.Generic.Dictionary`2");
        p.MethodName.Should().Be("TryGetValue");
        p.GenericTypeArguments!.Type.Should().Equal("System.Int32", "System.String");
    }

    [Fact]
    public void Parser_BothAxesGeneric_ExtractsTypeAndMethodArgs()
    {
        var p = EventPipeCpuSampler.ParseFullMethodName("MyApp.Cache`1[System.String].Get<System.Int32>");
        p.TypeFullName.Should().Be("MyApp.Cache`1");
        p.MethodName.Should().Be("Get");
        p.GenericArity.Should().Be(1);
        p.GenericTypeArguments!.Type.Should().Equal("System.String");
        p.GenericTypeArguments.Method.Should().Equal("System.Int32");
    }

    [Fact]
    public void Parser_NestedGenericArg_PreservesInnerBrackets()
    {
        // List<Dictionary<int,string>> as a method-level type arg.
        var p = EventPipeCpuSampler.ParseFullMethodName(
            "MyApp.Helper.Echo<System.Collections.Generic.Dictionary`2[System.Int32,System.String]>");
        p.MethodName.Should().Be("Echo");
        p.GenericTypeArguments!.Method.Should().HaveCount(1);
        p.GenericTypeArguments.Method[0].Should().Be(
            "System.Collections.Generic.Dictionary`2[System.Int32,System.String]");
    }

    [Fact]
    public void Parser_ArraySignature_NotConfusedWithTypeArgs()
    {
        // `byte[]` ends in `]` but there's no preceding backtick — must NOT be treated as
        // a type-args block. The full name "MyApp.Helper.Take[]" is degenerate but the
        // realistic case is when an inner segment contains `[]` from an array signature.
        var p = EventPipeCpuSampler.ParseFullMethodName("MyApp.Helper.Take");
        p.TypeFullName.Should().Be("MyApp.Helper");
        p.MethodName.Should().Be("Take");
        p.GenericTypeArguments.Should().BeNull();
    }

    [Fact]
    public void Parser_NullOrEmpty_ReturnsEmptyTriple()
    {
        var p = EventPipeCpuSampler.ParseFullMethodName(null);
        p.MethodName.Should().Be(string.Empty);
        p.GenericTypeArguments.Should().BeNull();
    }

    [Fact]
    public void Parser_StripsTrailingParameterSignature_TopLevelMain()
    {
        // Regression for #31: minimal-API / top-level-statement synthesized entry point.
        // Without param-signature stripping the inner `.` of `System.String[]` was chosen
        // by FindLastTopLevelDot, splitting "Program.<Main>$(class System" / "String[])".
        var p = EventPipeCpuSampler.ParseFullMethodName("Program.<Main>$(class System.String[])");
        p.TypeFullName.Should().Be("Program");
        p.MethodName.Should().Be("<Main>$");
        p.GenericArity.Should().Be(0);
        p.GenericTypeArguments.Should().BeNull();
    }

    [Fact]
    public void Parser_StripsTrailingParameterSignature_GenericMethodWithParams()
    {
        // Method-level generics + params: ensure the trailing `(...)` is stripped before
        // the trailing-`>` branch parses method args, so `Echo<int>` survives.
        var p = EventPipeCpuSampler.ParseFullMethodName(
            "MyApp.Helper.Echo<System.Int32>(class System.Int32)");
        p.TypeFullName.Should().Be("MyApp.Helper");
        p.MethodName.Should().Be("Echo");
        p.GenericArity.Should().Be(1);
        p.GenericTypeArguments!.Method.Should().Equal("System.Int32");
    }

    [Fact]
    public void Parser_StripsTrailingParameterSignature_DottedParamType()
    {
        var p = EventPipeCpuSampler.ParseFullMethodName(
            "MyApp.OrderService.Process(class MyApp.Models.Order)");
        p.TypeFullName.Should().Be("MyApp.OrderService");
        p.MethodName.Should().Be("Process");
        p.GenericArity.Should().Be(0);
    }

    [Theory]
    // Regression for #60: dogfooding against assembly-mcp surfaced four real CPU-sample
    // hotspot frames where commas in the IL parameter signature were suspected of bleeding
    // into the typeFullName / methodName split. Lock the contract here so the parser cannot
    // silently regress on multi-arg, dotted, nested, or pointer-typed parameter lists.
    [InlineData("System.Threading.Monitor.Wait(class System.Object,int32)", "System.Threading.Monitor", "Wait")]
    [InlineData("Interop+Sys.Read(class System.Runtime.InteropServices.SafeHandle,unsigned int8*,int32)", "Interop+Sys", "Read")]
    [InlineData("System.Threading.Thread+StartHelper.Callback(class System.Object)", "System.Threading.Thread+StartHelper", "Callback")]
    [InlineData("System.Threading.Tasks.Task.InternalWait(int32,value class System.Threading.CancellationToken)", "System.Threading.Tasks.Task", "InternalWait")]
    public void Parser_60_CommaInParameterSignature_DoesNotBleedIntoIdentity(string fullName, string expectedType, string expectedMethod)
    {
        var p = EventPipeCpuSampler.ParseFullMethodName(fullName);
        p.TypeFullName.Should().Be(expectedType);
        p.MethodName.Should().Be(expectedMethod);
        p.GenericArity.Should().Be(0);
        p.GenericTypeArguments.Should().BeNull();
    }

    [Fact]
    public void Parser_TopLevelMain_WithoutParams_HasZeroArity()
    {
        // Regression for #34. <Main>$ uses < and > as part of the synthesized name; the
        // trailing-`>` method-generic branch must NOT treat <Main> as a method-arg list.
        var p = EventPipeCpuSampler.ParseFullMethodName("Program.<Main>$");
        p.TypeFullName.Should().Be("Program");
        p.MethodName.Should().Be("<Main>$");
        p.GenericArity.Should().Be(0);
        p.GenericTypeArguments.Should().BeNull();
    }

    [Theory]
    // Regression for #69 — compiler-generated identifiers that end in `>` (no suffix
    // after the closing bracket) must NOT be parsed as method-level generic arg lists.
    // Pattern: the `<` is preceded by `.` (or start of string), not by an identifier
    // character, so the trailing `<...>` belongs to the identifier itself.
    [InlineData("Program.<Main>", "Program", "<Main>")]
    [InlineData("Outer.<Method>d__0", "Outer", "<Method>d__0")]
    [InlineData("Outer.<>c__DisplayClass5_0", "Outer", "<>c__DisplayClass5_0")]
    [InlineData("Outer.<>c__DisplayClass5_0.<Lambda>b__0", "Outer.<>c__DisplayClass5_0", "<Lambda>b__0")]
    [InlineData("Outer.<MyLocalFunc>g__Local|0_0", "Outer", "<MyLocalFunc>g__Local|0_0")]
    public void Parser_CompilerGeneratedAngleBrackets_NotTreatedAsGenericArgs(
        string fullName, string expectedType, string expectedMethod)
    {
        var p = EventPipeCpuSampler.ParseFullMethodName(fullName);
        p.TypeFullName.Should().Be(expectedType);
        p.MethodName.Should().Be(expectedMethod);
        p.GenericArity.Should().Be(0);
        p.GenericTypeArguments.Should().BeNull();
    }

    [Fact]
    public void Parser_RealMethodGenericInstantiation_StillRecognized()
    {
        // Guards the #69 fix from over-shooting: the `<` here IS preceded by an
        // identifier character (`o` from `Echo`), so the bracketed text is a genuine
        // method-level generic-arg list and must be parsed as such.
        var p = EventPipeCpuSampler.ParseFullMethodName("MyApp.Helper.Echo<System.Int32>");
        p.TypeFullName.Should().Be("MyApp.Helper");
        p.MethodName.Should().Be("Echo");
        p.GenericArity.Should().Be(1);
        p.GenericTypeArguments.Should().NotBeNull();
        p.GenericTypeArguments!.Method.Should().Equal("System.Int32");
    }

    [Fact]
    public void GenericInstantiation_RoundTripsThroughJsonContext()
    {
        var inst = new GenericInstantiation(new[] { "System.Int32" }, new[] { "System.String" });
        var id = new MethodIdentity(
            ModuleName: "MyApp.dll",
            ModulePath: null,
            ModuleVersionId: Guid.Parse("33333333-3333-3333-3333-333333333333"),
            MetadataToken: 0x06000123,
            TypeFullName: "MyApp.Cache`1",
            MethodName: "Get",
            GenericArity: 1) { GenericTypeArguments = inst };

        var json = System.Text.Json.JsonSerializer.Serialize(
            id, InvestigationSummaryJsonContext.Default.MethodIdentity);
        json.Should().Contain("\"GenericTypeArguments\"");
        json.Should().Contain("\"System.Int32\"");
        json.Should().Contain("\"System.String\"");

        var roundtripped = System.Text.Json.JsonSerializer.Deserialize(
            json, InvestigationSummaryJsonContext.Default.MethodIdentity);
        roundtripped!.GenericTypeArguments.Should().NotBeNull();
        roundtripped.GenericTypeArguments!.Type.Should().Equal("System.Int32");
        roundtripped.GenericTypeArguments.Method.Should().Equal("System.String");
    }

    [Fact]
    public void MethodIdentity_CarriesSourceLocation_WhenStampedByProducer()
    {
        // Issue #28 — Source travels on the identity itself so every consumer (hotspots,
        // thread frames, exception frames, retention paths) sees it without a separate map.
        var src = new SourceLocation(
            File: "/abs/path/HotPath.cs",
            StartLine: 42,
            SourceLink: "https://github.com/me/repo/blob/abc123/HotPath.cs#L42",
            EndLine: 58);
        var id = new MethodIdentity(
            ModuleName: "MyApp.dll",
            ModulePath: "/app/MyApp.dll",
            ModuleVersionId: Guid.Parse("44444444-4444-4444-4444-444444444444"),
            MetadataToken: 0x06000150,
            TypeFullName: "MyApp.HotPath",
            MethodName: "DoWork",
            GenericArity: 0) { Source = src };

        id.Source.Should().NotBeNull();
        id.Source!.File.Should().Be("/abs/path/HotPath.cs");
        id.Source.StartLine.Should().Be(42);
        id.Source.EndLine.Should().Be(58);
        id.Source.SourceLink.Should().StartWith("https://");

        var json = System.Text.Json.JsonSerializer.Serialize(
            id, InvestigationSummaryJsonContext.Default.MethodIdentity);
        json.Should().Contain("\"Source\"");
        json.Should().Contain("58");

        var roundtripped = System.Text.Json.JsonSerializer.Deserialize(
            json, InvestigationSummaryJsonContext.Default.MethodIdentity);
        roundtripped!.Source!.StartLine.Should().Be(42);
        roundtripped.Source.EndLine.Should().Be(58);
    }

    [Fact]
    public void SourceLocation_EndLine_DefaultsToNull_ForBackwardCompat()
    {
        // Existing positional callers (pre-#28) construct SourceLocation without EndLine —
        // it's optional with a null default so no downstream code breaks.
        var src = new SourceLocation("/x/y.cs", 10, null);
        src.EndLine.Should().BeNull();
    }

    private sealed class FixedProv : IProvenanceCollector
    {
        public InvestigationProvenance Collect(int processId, string? buildAssemblyName = null)
            => new(Hostname: "test") { Build = null, Container = null };
    }
}
