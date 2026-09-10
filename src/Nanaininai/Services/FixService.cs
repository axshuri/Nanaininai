using Nanaininai.Core.Abstractions;
using Nanaininai.Core.Models;

namespace Nanaininai.Services;

/// <summary>
/// Turns diagnostic findings into concrete, previewable fixes.
/// Every fix goes through the same preview → confirm → apply pipeline.
/// </summary>
public sealed class FixService
{
    private readonly AgentContext _ctx;
    public FixService(AgentContext ctx) => _ctx = ctx;

    public async Task<OperationPlan> BuildPlanAsync(string fixId, CancellationToken ct = default)
    {
        var plan = new OperationPlan { Title = fixId };
        switch (fixId)
        {
            case "firewall.enableRequiredRules":
                plan.Title = "Enable required LAN firewall rules";
                var required = await _ctx.Firewall.GetRequiredRulesAsync(ct).ConfigureAwait(false);
                foreach (var (display, rule) in required.Where(r => r.Rule is null || !r.Rule.Enabled))
                    plan.Changes.Add(new PlannedChange { Index = plan.Changes.Count + 1, Category = "Firewall", Description = $"Enable rule: {rule?.Name ?? display}" });
                break;

            case "smb.enableFileSharing":
                plan.Title = "Enable File and Printer Sharing";
                plan.Changes.Add(new PlannedChange { Index = 1, Category = "Services", Description = "Start the 'Server' (LanmanServer) service, startup type: Automatic" });
                plan.Changes.Add(new PlannedChange { Index = 2, Category = "SMB", Description = "Enable SMB2 protocol (SMB1 stays disabled)" });
                plan.Changes.Add(new PlannedChange { Index = 3, Category = "Firewall", Description = "Enable 'File and Printer Sharing' inbound rules" });
                break;

            case "network.setPrivate":
                plan.Title = "Change network profile to Private";
                plan.Warning = "Only change this on a trusted LAN. The current profile is Public.";
                plan.Changes.Add(new PlannedChange { Index = 1, Category = "Profile", Description = "Set the active network connection profile to Private" });
                break;

            default:
                plan.Changes.Add(new PlannedChange { Index = 1, Category = "?", Description = $"Unknown fix '{fixId}'" });
                break;
        }
        return plan;
    }

    public async Task<OperationResult> ApplyAsync(string fixId, OperationContext ctx, CancellationToken ct = default)
    {
        switch (fixId)
        {
            case "firewall.enableRequiredRules":
            {
                if (ctx.DryRun) return await DryRunAsync(fixId, ct).ConfigureAwait(false);
                var required = await _ctx.Firewall.GetRequiredRulesAsync(ct).ConfigureAwait(false);
                var results = new List<string>();
                var allOk = true;
                foreach (var (_, rule) in required.Where(r => r.Rule is not null && !r.Rule.Enabled))
                {
                    var r = await _ctx.Firewall.EnableRuleAsync(rule!.Name, ctx, ct).ConfigureAwait(false);
                    allOk &= r.Success;
                    results.Add(r.Message);
                }
                return allOk
                    ? OperationResult.Ok("Required firewall rules enabled.", string.Join(Environment.NewLine, results))
                    : OperationResult.Fail("Some rules could not be enabled.", string.Join(Environment.NewLine, results));
            }

            case "smb.enableFileSharing":
                if (ctx.DryRun) return await DryRunAsync(fixId, ct).ConfigureAwait(false);
                var smbResult = await _ctx.Smb.EnableFileSharingAsync(ctx, ct).ConfigureAwait(false);
                if (smbResult.Success)
                {
                    var fw = await _ctx.Firewall.EnableRuleAsync("File and Printer Sharing (SMB-In)", ctx, ct).ConfigureAwait(false);
                    return OperationResult.Ok(smbResult.Message, smbResult.Details + Environment.NewLine + fw.Message);
                }
                return smbResult;

            case "network.setPrivate":
                if (ctx.DryRun) return await DryRunAsync(fixId, ct).ConfigureAwait(false);
                return await _ctx.NetworkConfig.SetNetworkCategoryAsync("Private", ctx, ct).ConfigureAwait(false);

            default:
                return OperationResult.Fail($"Unknown fix '{fixId}'.");
        }
    }

    private async Task<OperationResult> DryRunAsync(string fixId, CancellationToken ct)
    {
        var plan = await BuildPlanAsync(fixId, ct).ConfigureAwait(false);
        return OperationResult.Ok("DRY RUN - no changes were made.", plan.Render(dryRun: true));
    }
}
