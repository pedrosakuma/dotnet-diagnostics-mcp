using DotnetDiagnosticsMcp.Core.Security;
using FluentAssertions;
using Xunit;

namespace DotnetDiagnosticsMcp.Core.Tests;

public sealed class SensitiveDataRedactorTests
{
    [Theory]
    [InlineData("Authorization: Bearer eyJabcdefghij.payloadpart.signaturepart", "Bearer eyJ")]
    [InlineData("connection=\"Server=x;Password=hunter2;Database=foo\"", "Password=hunter2")]
    [InlineData("aws=AKIAIOSFODNN7EXAMPLE plus tail", "AKIAIOSFODNN7EXAMPLE")]
    [InlineData("github=ghp_abcdefghijklmnopqrstuvwxyz0123 trailing", "ghp_abcdefghijklmnopqrstuvwxyz0123")]
    [InlineData("api_key=supersecretvalue;extra", "api_key=supersecretvalue")]
    [InlineData("-----BEGIN RSA PRIVATE KEY-----\nMIIBOQ...", "-----BEGIN RSA PRIVATE KEY-----")]
    public void Redact_ReplacesKnownSecretShapes(string input, string sensitiveFragment)
    {
        var redactor = new SensitiveDataRedactor();
        var result = redactor.Redact(input);
        result.Should().NotBeNull();
        result.Should().NotContain(sensitiveFragment, "the sensitive substring must be replaced");
        result.Should().Contain(SensitiveDataRedactor.RedactedPlaceholder);
    }

    [Fact]
    public void Redact_PassesBenignContentThrough()
    {
        var redactor = new SensitiveDataRedactor();
        redactor.Redact("hello world").Should().Be("hello world");
        redactor.Redact("").Should().Be("");
        redactor.Redact(null).Should().BeNull();
    }

    [Fact]
    public void Redact_HonoursExtraPatterns()
    {
        var redactor = new SensitiveDataRedactor(new SecurityOptions
        {
            RedactionPatterns = { @"CUSTOMSECRET-\d+" },
        });

        redactor.Redact("see CUSTOMSECRET-12345 here")
            .Should().Be($"see {SensitiveDataRedactor.RedactedPlaceholder} here");
    }
}
