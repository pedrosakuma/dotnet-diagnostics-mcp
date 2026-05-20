using DotnetDiagnosticsMcp.Core.JitCapture;
using FluentAssertions;

namespace DotnetDiagnosticsMcp.Core.Tests;

public class JitMethodCapturerUnitTests
{
    [Fact]
    public async Task CaptureLiveAsync_RejectsNonPositivePid()
    {
        var capturer = new ClrMdJitMethodCapturer();
        var req = new MethodCaptureRequest(Guid.NewGuid(), 0x06000001);

        await FluentActions
            .Awaiting(() => capturer.CaptureLiveAsync(0, req))
            .Should().ThrowAsync<ArgumentOutOfRangeException>();

        await FluentActions
            .Awaiting(() => capturer.CaptureLiveAsync(-1, req))
            .Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task CaptureLiveAsync_RejectsNullRequest()
    {
        var capturer = new ClrMdJitMethodCapturer();

        await FluentActions
            .Awaiting(() => capturer.CaptureLiveAsync(1234, null!))
            .Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task CaptureFromDumpAsync_RejectsMissingFile()
    {
        var capturer = new ClrMdJitMethodCapturer();
        var req = new MethodCaptureRequest(Guid.NewGuid(), 0x06000001);
        var missing = Path.Combine(Path.GetTempPath(), $"does-not-exist-{Guid.NewGuid():N}.dmp");

        await FluentActions
            .Awaiting(() => capturer.CaptureFromDumpAsync(missing, req))
            .Should().ThrowAsync<FileNotFoundException>();
    }

    [Fact]
    public async Task CaptureFromDumpAsync_RejectsEmptyPath()
    {
        var capturer = new ClrMdJitMethodCapturer();
        var req = new MethodCaptureRequest(Guid.NewGuid(), 0x06000001);

        await FluentActions
            .Awaiting(() => capturer.CaptureFromDumpAsync("", req))
            .Should().ThrowAsync<ArgumentException>();

        await FluentActions
            .Awaiting(() => capturer.CaptureFromDumpAsync(null!, req))
            .Should().ThrowAsync<ArgumentException>();
    }
}
