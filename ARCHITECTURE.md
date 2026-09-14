# Execution and Scale Notes

## Invocation flow

1. `Program` parses a CLI request and validates the selected environment.
2. `GraphSharePointRepository.GetAsync` follows every Graph `@odata.nextLink`, maps list fields, and locally filters only active matching tests.
3. `ExecutionEngine` validates a definition, builds the request, executes Playwright, evaluates every rule, and publishes the normalized result.
4. `IExecutionProgressSink` receives each completed result; UI implementations can consume this seam without referencing Playwright or SharePoint.
5. The repository writes deterministic attempt titles (`ExecutionId:TestCaseId:Attempt`) so retries have an audit correlation key.

## Reliability behavior

- Invalid test data is **Invalid**, not an API functional failure.
- `HttpRequestException`, timeout, and task-cancellation transport errors are retry candidates; externally requested cancellation is propagated immediately.
- Each response payload is masked and bounded before persistence.
- A SharePoint publication failure is retained separately from the API/validation outcome and makes the CLI exit non-zero.
- Graph response failures retain status and up to 1,000 characters of diagnostic content for troubleshooting.

## Scale boundaries and operational controls

- The runner now follows Graph paging and supports bounded API parallelism. Set `--parallelism` to match target-API and SharePoint capacity; fail-fast intentionally uses sequential execution to avoid launching work that cannot be cancelled safely.
- The current persistence policy is direct write-through. It is appropriate for small/medium suites; use a queue/batch implementation of `IResultRepository` for large suites or when Graph throttling becomes material.
- Run lifecycle records are append-only snapshots. Configure SharePoint retention/archival to avoid degraded list queries.
- JSON-path validation handles nested object properties. Array traversal and JSON schema validation should be added as new rule handlers, not embedded in test cases.
