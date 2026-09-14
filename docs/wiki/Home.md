# Playwright API Automation Framework Wiki

> **Purpose:** A reusable .NET 8 framework that executes configuration-driven API tests with Playwright, stores test definitions in SharePoint, and records execution history through Microsoft Graph.

## Contents

1. [Architecture and execution lifecycle](#architecture-and-execution-lifecycle)
2. [Prerequisites and local setup](#prerequisites-and-local-setup)
3. [Configuration and secrets](#configuration-and-secrets)
4. [SharePoint data contract](#sharepoint-data-contract)
5. [Validation rule reference](#validation-rule-reference)
6. [Running test suites](#running-test-suites)
7. [Results, logging, and retention](#results-logging-and-retention)
8. [UI integration](#ui-integration)
9. [CI/CD guidance](#cicd-guidance)
10. [Troubleshooting](#troubleshooting)
11. [Extension guide](#extension-guide)

---

## Architecture and execution lifecycle

The console host composes the following replaceable components:

| Component | Responsibility | Default implementation |
| --- | --- | --- |
| Test-case provider | Retrieves test definitions | `GraphSharePointRepository` |
| Result repository | Persists run and per-case outcomes | `GraphSharePointRepository` |
| Request builder | Resolves URLs, parameters, headers, and auth | `DynamicRequestBuilder` |
| API client | Sends HTTP requests | `PlaywrightApiClient` |
| Validation engine | Evaluates response rules | `JsonValidationEngine` |
| Progress sink | Displays progress/results | `ConsoleProgressSink` |

```text
CLI / future GUI / CI pipeline
             |
             v
      ExecutionEngine
       |       |       \
       v       v        v
 SharePoint  Playwright  Progress sink
 definitions API client  (console/UI)
       |       |
       +-- validation +-- result publication to SharePoint
```

For each active selected test case, the engine:

1. Generates an execution ID for the run and creates an initial run record.
2. Retrieves and validates the test definition before an HTTP request is sent.
3. Builds a request with substituted path parameters, encoded query parameters, configured headers, bearer token injection, and `X-Correlation-ID`.
4. Sends the request through an isolated Playwright API request context.
5. Captures status, headers, and body; executes all configured validation rules.
6. Masks common sensitive key/value patterns and bounds the stored response content.
7. Persists an immutable execution attempt identified by `ExecutionId:TestCaseId:Attempt`.
8. Reports progress to the configured presentation sink.

A functional API/validation failure is distinct from a **publication failure**. A test can pass functionally but still cause a non-zero process exit when its result could not be written to SharePoint.

## Prerequisites and local setup

- .NET SDK 8.0 or later.
- Access to the target APIs.
- A Microsoft Graph access token with least-privilege permissions for the configured SharePoint site and lists.
- Playwright browser/runtime dependencies installed for the operating system, if required by the target environment.

```bash
dotnet restore
dotnet build ApiAutomation.sln
pwsh ApiAutomation/bin/Debug/net8.0/playwright.ps1 install
```

Set secrets in the shell, a supported local secret store, or the CI/CD platform—never in `appsettings.json` or SharePoint list fields:

```bash
# PowerShell
$env:SHAREPOINT_ACCESS_TOKEN = "<Microsoft-Graph-token>"
$env:API_QA_BEARER_TOKEN = "<target-api-token>"

# bash
export SHAREPOINT_ACCESS_TOKEN='<Microsoft-Graph-token>'
export API_QA_BEARER_TOKEN='<target-api-token>'
```

## Configuration and secrets

`ApiAutomation/appsettings.json` contains non-secret application configuration. Environment definitions are keyed by the value supplied to `--environment`.

```json
{
  "MaxRetries": 2,
  "ResponseBodyLimit": 65000,
  "Environments": {
    "qa": {
      "Name": "qa",
      "BaseUrl": "https://api.example.test",
      "TimeoutMs": 30000,
      "BearerTokenSecret": "API_QA_BEARER_TOKEN",
      "DefaultHeaders": { "Accept": "application/json" }
    }
  },
  "SharePoint": {
    "SiteId": "<site-id>",
    "TestCasesListId": "<test-cases-list-id>",
    "ExecutionsListId": "<executions-list-id>",
    "RunsListId": "<runs-list-id>",
    "AccessTokenSecret": "SHAREPOINT_ACCESS_TOKEN"
  }
}
```

| Setting | Meaning |
| --- | --- |
| `MaxRetries` | Maximum additional retries for transient technical failures. Validation failures are not retried. |
| `ResponseBodyLimit` | Maximum number of response characters retained in a result before truncation. |
| `BaseUrl` | Environment-specific API base URL. Keep it out of individual test definitions. |
| `BearerTokenSecret` | Environment variable name containing the API bearer token. |
| `AccessTokenSecret` | Environment variable name containing the Microsoft Graph access token. |

## SharePoint data contract

### API Test Cases list

Use the following **internal SharePoint field names**. `Title` is the test-case identifier.

| Field | Required | Format / example |
| --- | --- | --- |
| `Title` | Yes | `TC-CUSTOMER-001` |
| `Suite`, `ApiName`, `Scenario` | Yes | Text grouping/filter values |
| `Endpoint` | Yes | `/customers/{customerId}` |
| `HttpMethod` | Yes | `GET`, `POST`, `PUT`, `PATCH`, or `DELETE` |
| `Active` | Yes | `true` |
| `Priority` | No | Integer; lower runs first |
| `ExpectedStatusCode` | Yes | `200` |
| `Headers` | No | JSON object: `{"Accept":"application/json"}` |
| `QueryParameters` | No | JSON object: `{"include":"orders"}` |
| `PathParameters` | No | JSON object: `{"customerId":"123"}` |
| `Payload` | No | Valid JSON request body |
| `ValidationRules` | No | JSON array described below |
| `Authentication` | No | Reserved for a future authentication-provider implementation |
| `SchemaVersion` | No | Integer; default is `1` |

Example definition:

```json
{
  "Title": "TC-CUSTOMER-001",
  "Suite": "Smoke",
  "ApiName": "Customer API",
  "Endpoint": "/customers/{customerId}",
  "HttpMethod": "GET",
  "Scenario": "Read active customer",
  "Active": true,
  "Priority": 10,
  "ExpectedStatusCode": 200,
  "Headers": "{\"Accept\":\"application/json\"}",
  "PathParameters": "{\"customerId\":\"123\"}",
  "ValidationRules": "[{\"id\":\"status\",\"type\":\"json\",\"path\":\"$.status\",\"expectedValue\":\"Active\",\"operator\":\"equals\"}]"
}
```

### Execution and run lists

The framework creates one immutable execution item for each completed test-case attempt and two append-only run lifecycle snapshots (`Running` and completion). Provision fields matching the names written by `GraphSharePointRepository`, including `ExecutionId`, `TestCaseId`, `Attempt`, `Environment`, `ApiName`, `Scenario`, `HttpMethod`, `Endpoint`, `Status`, `ActualStatusCode`, `ExpectedStatusCode`, `ValidationStatus`, `ValidationSummary`, `FailureReason`, `ResponsePayload`, `DurationMs`, `RetryCount`, `StartedAt`, and `CompletedAt`.

> **Retention note:** execution history grows quickly. Index the fields used in business queries, archive old records, and keep highly queried active lists below organizational SharePoint list-performance thresholds.

## Validation rule reference

Each rule is a JSON object with `id`, `type`, `path`, `expectedValue`, and optional `operator`.

| Type | Path source | Supported operators | Example |
| --- | --- | --- | --- |
| `json` / `jsonequals` | Dot-delimited JSON property path | `equals`, `contains`, `exists`, `not-null`, `greater-than` | `{"id":"status","type":"json","path":"$.customer.status","expectedValue":"Active","operator":"equals"}` |
| `header` | HTTP response header name | Same operators | `{"id":"content-type","type":"header","path":"content-type","expectedValue":"application/json","operator":"contains"}` |

Status-code validation is always performed from `ExpectedStatusCode`, whether or not the rule list is empty. Every rule returns its own pass/fail diagnostic; all configured rules are evaluated so a single response can explain multiple mismatches.

Current JSON path support traverses nested objects (for example `$.customer.address.postcode`). Array indexing, XML, and JSON Schema are planned extension points and should be implemented as new rule handlers.

## Running test suites

```bash
# All active cases in a suite
dotnet run --project ApiAutomation -- --environment qa --suite Smoke

# A selected API, scenario, or case
dotnet run --project ApiAutomation -- --environment qa --api "Customer API"
dotnet run --project ApiAutomation -- --environment qa --scenario "Read active customer"
dotnet run --project ApiAutomation -- --environment qa --test-case TC-CUSTOMER-001

# Controlled parallelism
dotnet run --project ApiAutomation -- --environment qa --suite Regression --parallelism 4

# Stop scheduling subsequent cases after the first functional/invalid/error result
dotnet run --project ApiAutomation -- --environment qa --suite Smoke --fail-fast
```

Exit codes:

| Code | Meaning |
| --- | --- |
| `0` | All executed cases passed and results were published. |
| `1` | At least one failed/invalid/error case or result-publication failure. |
| `2` | Framework/configuration/argument startup error. |

## Results, logging, and retention

- Test results are written after every test case, including invalid data and execution errors.
- Response content is capped by `ResponseBodyLimit`; the engine marks retained content as truncated when necessary.
- The engine masks conventional `authorization`, `token`, `password`, and `secret` key/value patterns. Add an organization-specific masking layer before enabling response persistence for PII or domain-sensitive data.
- Use execution IDs and attempt IDs when correlating console, API, and SharePoint records.
- Preserve failures and publication errors longer than routine successful records; agree archival and purge intervals with data owners.

## UI integration

The current `ConsoleProgressSink` provides a lightweight terminal view. `IExecutionProgressSink` is intentionally UI-agnostic:

```csharp
public interface IExecutionProgressSink
{
    void Report(TestExecutionResult result);
    void Complete(ExecutionSummary summary);
}
```

A desktop or web UI should implement this interface and marshal updates onto its own UI dispatcher. The engine serializes sink callbacks, so it is safe to use controlled parallel API execution without concurrent UI notifications. A full GUI should provide:

- Environment, suite, API, scenario, and case selectors.
- Start, cancellation, and controlled-parallelism controls.
- Live totals plus passed/failed/invalid/publication-failure views.
- Drill-down to masked request/response, validation diagnostics, and SharePoint execution IDs.
- Historical execution-result search using indexed SharePoint fields.

## CI/CD guidance

Use the same CLI in a pipeline and pass non-secret filters as arguments. Resolve Graph and API tokens through the pipeline secret store.

```yaml
- script: dotnet run --project ApiAutomation -- --environment qa --suite Regression --parallelism 2
  displayName: Run API regression
  env:
    SHAREPOINT_ACCESS_TOKEN: $(sharepoint-access-token)
    API_QA_BEARER_TOKEN: $(api-qa-bearer-token)
```

Start with sequential execution, observe target API and Graph throttling, then increment `--parallelism` conservatively. Direct per-case SharePoint write-through is simple and preserves partial history, but large suites may require a queue/batch `IResultRepository` implementation.

## Troubleshooting

| Symptom | Check | Resolution |
| --- | --- | --- |
| `Missing secret` | Required environment variable | Set `SHAREPOINT_ACCESS_TOKEN` or the configured bearer-token secret in the execution process. |
| Graph 401/403 | Token scope and list/site access | Obtain an approved Graph token and grant least-privilege access to the configured site/lists. |
| Graph 429/5xx | Throttling or service health | Reduce parallelism; retry technical failures; inspect bounded Graph error details. |
| `Invalid` test case | SharePoint field values | Verify endpoint, HTTP method, expected status, JSON payload, and rule operation/type. |
| JSON validation path missing | Response shape/rule path | Compare actual response with the configured dot path; `$` is optional. |
| Result publication failure | Execution list schema/access | Verify list ID, internal column names, access token permissions, and response-content size limits. |
| Playwright runtime issue | Build output / browser dependencies | Restore packages and run the Playwright install script for the current target framework. |

## Extension guide

Follow the existing interfaces rather than adding test-specific branches to the engine:

- Add authentication types by introducing an authentication provider consumed by `IRequestBuilder`.
- Add XML, array-path, JSON Schema, or domain assertions as validation-engine rule handlers.
- Add durable local queue/batch persistence by implementing `IResultRepository`.
- Add a WPF, MAUI, or Blazor presentation layer by implementing `IExecutionProgressSink` and hosting the same execution engine.
- Add historical reporting through a read model/repository that queries the execution list with indexed Graph filters.

Keep secrets external, preserve result correlation IDs, and add unit tests for every new rule, transport behavior, and persistence strategy.
