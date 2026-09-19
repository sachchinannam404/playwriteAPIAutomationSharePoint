# Execution and Scale Notes

## Invocation flow

1. `Program` parses a CLI request and validates the selected environment.
2. `GraphSharePointRepository.GetAsync` applies a server-side `Active` filter, follows every Graph `@odata.nextLink`, maps list fields, and locally filters remaining criteria.
3. `ExecutionEngine` validates a definition, builds the request, executes Playwright, evaluates every rule, and publishes the normalized result.
4. `IExecutionProgressSink` receives each completed result; UI implementations can consume this seam without referencing Playwright or SharePoint.
5. The repository writes deterministic attempt titles (`ExecutionId:TestCaseId:Attempt`) so retries have an audit correlation key.

## Reliability behavior

- Invalid test data is **Invalid**, not an API functional failure.
- Transport exceptions (`HttpRequestException`, timeout, non-user cancellation) and configured status codes (429, 5xx, …) are retry candidates with exponential backoff + jitter.
- Externally requested cancellation is propagated immediately.
- Each response payload is masked and bounded before persistence.
- A SharePoint publication failure is retained separately from the API/validation outcome and makes the CLI exit non-zero.
- Graph response failures retain status and up to 1,000 characters of diagnostic content for troubleshooting.

## Authentication

- `ITokenProvider` is the extension point for Graph tokens.
- Default: `EnvironmentTokenProvider` (static env var) — suitable for local and pipeline smoke runs.
- Production: implement `ITokenProvider` with MSAL confidential client (certificate preferred) and inject it in `Program.cs`.

## Scale boundaries and operational controls

- The runner follows Graph paging and supports bounded API parallelism. Set `--parallelism` to match target-API and SharePoint capacity; fail-fast intentionally uses sequential execution to avoid launching work that cannot be cancelled safely.
- The current persistence policy is direct write-through. It is appropriate for small/medium suites; use a queue/batch implementation of `IResultRepository` for large suites or when Graph throttling becomes material.
- Run lifecycle records are append-only snapshots. Configure SharePoint retention/archival to avoid degraded list queries.
- JSON-path validation handles nested object properties and numeric array indexes (`items[0].id` or `items.0.id`). Wildcard/filter expressions and full JSON Schema validation should be added as new rule handlers, not embedded in test cases.
