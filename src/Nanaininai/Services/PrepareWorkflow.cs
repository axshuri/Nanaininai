using Nanaininai.Core.Models;
using Nanaininai.Core.Network;

namespace Nanaininai.Services;

/// <summary>The "PREPARE THIS PC" guided workflow: validate everything, list fixes.</summary>
public sealed class PrepareWorkflow
{
    private readonly AgentContext _ctx;
    public PrepareWorkflow(AgentContext ctx) => _ctx = ctx;

    public sealed class PrepareResult
    {
        public List<DiagnosticResult> Checks { get; set; } = new();
        public OperationPlan Fixes { get; set; } = new() { Title = "Recommended fixes" };
        public bool Ready => Checks.All(c => c.Status is Core.Models.DiagnosticStatus.Pass or Core.Models.DiagnosticStatus.Info);
        public bool HasWarnings => Checks.Any(c => c.Status is Core.Models.DiagnosticStatus.Warn);
    }

    public async Task<PrepareResult> RunAsync(IProgress<DiagnosticResult>? progress = null, CancellationToken ct = default)
    {
        var result = new PrepareResult();

        var checks = await _ctx.Diagnostics.RunAllAsync(progress, ct).ConfigureAwait(false);
        result.Checks = checks;

        // Aggregate fixable findings into one plan.
        var fixes = new OperationPlan { Title = "Recommended fixes for this PC" };
        var fixer = new FixService(_ctx);
        foreach (var check in checks.Where(c => c.FixId is not null).DistinctBy(c => c.FixId))
        {
            var plan = await fixer.BuildPlanAsync(check.FixId!, ct).ConfigureAwait(false);
            foreach (var change in plan.Changes)
                fixes.Changes.Add(new PlannedChange { Index = fixes.Changes.Count + 1, Category = change.Category, Description = change.Description });
        }

        // Computer name / workgroup advisory.
        var sys = await _ctx.SystemInfo.GetSystemInfoAsync(ct).ConfigureAwait(false);
        if (sys.ComputerName.StartsWith("DESKTOP-", StringComparison.OrdinalIgnoreCase) || sys.ComputerName.StartsWith("WIN-", StringComparison.OrdinalIgnoreCase))
            fixes.Changes.Add(new PlannedChange { Index = fixes.Changes.Count + 1, Category = "Naming", Description = $"Optional: rename computer '{sys.ComputerName}' to match the naming scheme (e.g. SCHOOL-PC-01) in the Naming section" });
        var wg = sys.Workgroup;
        if (!sys.IsDomainJoined && string.Equals(wg, "WORKGROUP", StringComparison.OrdinalIgnoreCase))
            fixes.Changes.Add(new PlannedChange { Index = fixes.Changes.Count + 1, Category = "Workgroup", Description = $"Optional: join a named workgroup (currently '{wg}') - see Workgroup instructions; change requires confirmation and a restart" });

        result.Fixes = fixes;
        _ctx.Log.Info("prepare.run", Environment.MachineName, $"checks={checks.Count} fixes={fixes.Changes.Count}");
        return result;
    }
}
