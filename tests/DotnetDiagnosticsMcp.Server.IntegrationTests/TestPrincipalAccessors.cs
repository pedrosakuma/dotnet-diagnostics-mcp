using System.Collections.Immutable;
using DotnetDiagnosticsMcp.Server.Security;

namespace DotnetDiagnosticsMcp.Server.IntegrationTests;

/// <summary>
/// Test helpers for B5.2 — every tool surface method now requires an
/// <see cref="IPrincipalAccessor"/> service parameter. Tests that exercise the
/// gated behavior directly inject one of these stubs rather than spinning up
/// a full WebApplicationFactory.
/// </summary>
internal static class TestPrincipalAccessors
{
    /// <summary>Synthetic root principal — passes every scope gate (parity with
    /// legacy <c>MCP_BEARER_TOKEN</c> bearer). Use for tests that do not care
    /// about per-scope behavior.</summary>
    public static readonly IPrincipalAccessor Root = new StubAccessor(new BearerPrincipal(
        name: "test-root",
        scopes: ImmutableHashSet.Create(BearerPrincipal.RootScope)));

    /// <summary>Builds an accessor whose principal holds exactly the supplied scopes
    /// (no implicit root). Pass an empty array for the "authenticated but
    /// unprivileged" case.</summary>
    public static IPrincipalAccessor WithScopes(params string[] scopes) =>
        new StubAccessor(new BearerPrincipal(
            name: "test-principal",
            scopes: scopes.Length == 0 ? ImmutableHashSet<string>.Empty : ImmutableHashSet.Create(scopes)));

    /// <summary>Accessor that yields no principal — exercises the "missing principal"
    /// branch the authorization filter rejects.</summary>
    public static readonly IPrincipalAccessor Anonymous = new StubAccessor(null);

    private sealed class StubAccessor : IPrincipalAccessor
    {
        public StubAccessor(BearerPrincipal? principal) { Current = principal; }
        public BearerPrincipal? Current { get; }
    }
}
