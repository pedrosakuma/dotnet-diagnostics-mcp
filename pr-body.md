## Summary

- add `inspect_process(view="requests-now")` to snapshot in-flight ASP.NET Core requests
- enrich each request with the observed thread id plus captured top stack frames
- add `/slow-hang?seconds=N` to `BadCodeSample` and live coverage for the new view
- document the new bootstrap view and the endpoint-hang investigation recipe

## Implementation notes

- new `RequestsNowCollector` watches `HttpRequestIn` activity start/stop pairs over a short EventPipe window
- request rows are only kept when the start event has no matching stop before the window closes
- per-request stack capture now happens immediately after the start event so async requests do not get a later unrelated thread stack attributed back to them
- `inspect_process` keeps legacy `read-counters` authorization for existing views and requires `ptrace` for `view="requests-now"`
- health-check tests now inject null writers so expected `FAIL /health` stderr output does not abort the full integration suite

## Verification

- `dotnet build DotnetDiagnosticsMcp.slnx -c Release`
- `dotnet test tests/DotnetDiagnosticsMcp.Core.Tests/ -c Release --no-build --filter "FullyQualifiedName~RequestsNow"`
- `dotnet test tests/DotnetDiagnosticsMcp.Server.IntegrationTests/ -c Release --no-build`

Closes #249.
