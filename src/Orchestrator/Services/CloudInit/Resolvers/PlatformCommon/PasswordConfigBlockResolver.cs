using System.Threading;
using System.Threading.Tasks;
using DeCloud.Orchestrator.Interfaces.CloudInit;
using DeCloud.Shared.Models;

namespace DeCloud.Orchestrator.Services.CloudInit.Resolvers.PlatformCommon;

/// <summary>
/// Resolves <c>__PASSWORD_CONFIG_BLOCK__</c> — the YAML chunk that sets the
/// root password via cloud-init's <c>chpasswd.users</c> module.
///
/// <para>
/// Source: <c>ctx.UserSuppliedStatics["ADMIN_PASSWORD"]</c>. The orchestrator's
/// <c>VmService</c> generates or accepts a password and surfaces it through
/// this key when calling the renderer. System VMs typically don't carry a
/// password and the resolver returns the empty-marker comment.
/// </para>
/// </summary>
public sealed class PasswordConfigBlockResolver : IVariableResolver
{
    public string ResolverKey => "PASSWORD_CONFIG_BLOCK";
    public VariableKind Kind => VariableKind.Static;

    public Task<string> ResolveAsync(ResolutionContext ctx, CancellationToken ct)
    {
        var password = ctx.UserSuppliedStatics.GetValueOrDefault("ADMIN_PASSWORD");
        return Task.FromResult(CloudInitFormatting.BuildPasswordBlock(password));
    }
}
