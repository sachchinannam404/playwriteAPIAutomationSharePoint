using ApiAutomation.Core.Interfaces;
using ApiAutomation.Core.Models;

namespace ApiAutomation.GUI;
/// <summary>Lightweight terminal presentation for live per-case progress and final run totals.</summary>
public sealed class ConsoleProgressSink : IExecutionProgressSink
{
    /// <summary>Writes one completed test result for CLI users and CI logs.</summary>
    public void Report(TestExecutionResult result) => Console.WriteLine($"[{result.Status,-7}] {result.TestCaseId} ({result.Duration.TotalMilliseconds:0} ms) publication={(result.PublicationSucceeded ? "ok" : "failed")}");
    /// <summary>Writes the final execution totals after all scheduled work completes.</summary>
    public void Complete(ExecutionSummary summary) => Console.WriteLine($"Run {summary.ExecutionId}: {summary.Passed} passed, {summary.Failed} failed, {summary.PublicationFailures} publication failures.");
}
