using DeCloud.Orchestrator.Interfaces.CloudInit;
using DeCloud.Shared.Models;

namespace DeCloud.Orchestrator.Services.CloudInit.Resolvers.PlatformCommon;

/// <summary>
/// Resolves <c>__SSH_PASSWORD_AUTH__</c> — the boolean string ("true"/"false")
/// for cloud-init's <c>ssh_pwauth</c> directive.
///
/// <para>
/// True iff <c>ctx.UserSuppliedStatics["ADMIN_PASSWORD"]</c> is non-empty —
/// you can't have password auth without a password. Yields the lowercase
/// strings cloud-init expects (<c>true</c>/<c>false</c>), not C#'s
/// <c>True</c>/<c>False</c>.
/// </para>
/// </summary>
public sealed class SshPasswordAuthResolver : IVariableResolver
{
    public string ResolverKey => "SSH_PASSWORD_AUTH";
    public VariableKind Kind => VariableKind.Static;

    public Task<string> ResolveAsync(ResolutionContext ctx, CancellationToken ct)
    {
        var hasPassword = !string.IsNullOrEmpty(
            ctx.UserSuppliedStatics.GetValueOrDefault("ADMIN_PASSWORD"));
        return Task.FromResult(hasPassword ? "true" : "false");
    }
}
