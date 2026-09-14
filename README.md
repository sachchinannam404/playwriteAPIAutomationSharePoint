# Playwright API Automation Framework

A .NET 8 console execution engine for data-driven API tests. Test definitions and execution history are read from and written to SharePoint through Microsoft Graph. Playwright executes the API calls; validation rules remain data, not test code.

## Decisions embodied by this starter

- **Runner:** custom data-driven runner (rather than static NUnit tests).
- **Persistence:** direct write-through, retrying transient SharePoint writes; a failed publication is reported separately from an API/validation failure.
- **GUI seam:** `IExecutionProgressSink` keeps the engine UI-independent. `ConsoleProgressSink` is the included lightweight terminal UI; a WPF/Blazor client can use the same engine later.

Confirm those choices, retention, Graph schema, API throttling limits, and GUI hosting requirements with stakeholders before production deployment.

## Configure

Copy `ApiAutomation/appsettings.json` and set environment variables for secrets. `SHAREPOINT_ACCESS_TOKEN` is required for Graph access. No secrets belong in source control.

## Run

```bash
dotnet restore
dotnet run --project ApiAutomation -- --environment qa --suite Smoke
```

Optional filters: `--api`, `--scenario`, `--test-case`, `--parallelism 1`, `--fail-fast`. Use `--help` for all options. The process exits non-zero for functional failures, invalid test data, or publication failures.

Install Playwright browsers after restore when required by your platform:

```bash
pwsh ApiAutomation/bin/Debug/net8.0/playwright.ps1 install
```

## SharePoint list contract

The Graph list named by `TestCasesListId` uses these field internal names: `Title` (test-case ID), `Suite`, `ApiName`, `Endpoint`, `HttpMethod`, `Scenario`, `Active`, `Priority`, `ExpectedStatusCode`, `Headers`, `QueryParameters`, `PathParameters`, `Payload`, `ValidationRules`, `Authentication`, and `SchemaVersion`. JSON fields use objects (for parameters) and an array of `{ id, type, path, expectedValue, operator }` rules. The execution and run list fields are the names emitted in `GraphSharePointRepository`; provision them before running.

Supported validation operations are `equals`, `contains`, `exists`, `not-null`, and `greater-than`. Rule types are `json*` (JSON path) and `header`. Invalid definitions result in **Invalid**, never an API functional failure.

`SHAREPOINT_ACCESS_TOKEN` must be a Microsoft Graph token with least-privilege access to the configured lists. Sensitive key/value patterns are masked before response/request persistence; configure additional organizational masking upstream for nested JSON secrets and PII.

## Test coverage

The unit test project covers nested JSON validation failures, malformed test-data rejection, URL/header construction, transient API retry behavior, and result-publication failure separation. Run it with `dotnet test` after installing the .NET SDK.

## SharePoint REST transport

`ApiAutomation.Data.Repositories.SharePointRestClient` is the shared Graph transport used by SharePoint repositories. It centralizes bearer-token resolution, JSON GET/POST/PATCH operations, cancellation propagation, and bounded Graph error diagnostics. New repository implementations should depend on this type rather than creating ad-hoc `HttpClient` instances.
