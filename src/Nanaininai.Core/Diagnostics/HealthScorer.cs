using Nanaininai.Core.Models;

namespace Nanaininai.Core.Diagnostics;

/// <summary>
/// Computes the transparent LAN health score. Every deduction is explicit and
/// carried into the report so nothing is ever hidden from the administrator.
/// </summary>
public static class HealthScorer
{
    /// <param name="checks">Each check with its status and the points it deducts when failing (0 when passing).</param>
    public static HealthReport Compute(IEnumerable<(string Name, DiagnosticStatus Status, int DeductionWhenFailing, string FailReason)> checks)
    {
        var report = new HealthReport();
        var score = HealthReport.MaxScore;

        foreach (var (name, status, deduction, reason) in checks)
        {
            var item = new HealthItem { Name = name, Status = status, Reason = reason };
            switch (status)
            {
                case DiagnosticStatus.Pass:
                    item.Deduction = 0;
                    break;
                case DiagnosticStatus.Warn:
                    item.Deduction = deduction;
                    item.Reason = reason;
                    break;
                case DiagnosticStatus.Fail:
                    item.Deduction = deduction * 2; // a hard failure costs double
                    item.Reason = reason;
                    break;
                case DiagnosticStatus.Skipped:
                    item.Deduction = 0;
                    item.Reason = "not checked";
                    break;
            }
            score -= item.Deduction;
            report.Items.Add(item);
        }

        report.Score = Math.Clamp(score, 0, HealthReport.MaxScore);
        return report;
    }
}
