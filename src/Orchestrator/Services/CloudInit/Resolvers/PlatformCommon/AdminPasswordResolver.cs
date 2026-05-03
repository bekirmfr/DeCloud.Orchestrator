using DeCloud.Orchestrator.Interfaces.CloudInit;
using DeCloud.Shared.Models;

namespace DeCloud.Orchestrator.Services.CloudInit.Resolvers.PlatformCommon;

/// <summary>
/// Resolves <c>__ADMIN_PASSWORD__</c> — the raw plaintext root password.
///
/// <para>
/// Used by tenant cloud-init bootcmd to set the password via <c>chpasswd</c>
/// alongside the <c>chpasswd.users</c> module. Same source as
/// <see cref="PasswordConfigBlockResolver"/> — so the two values stay in
/// sync and the bootcmd / chpasswd.users paths agree on the password.
/// </para>
///
/// <para>
/// <b>Trust boundary:</b> The cloud-init document is regarded as secret material
/// (carries SSH keys, identity material). Adding a plaintext password to the
/// same document does not lower the trust posture — anyone able to read the
/// rendered cloud-init can also extract SSH keys and forge VM identity.
/// </para>
/// </summary>
public sealed class AdminPasswordResolver : IVariableResolver
{
    public string ResolverKey => "ADMIN_PASSWORD";
    public VariableKind Kind => VariableKind.Static;

    public Task<string> ResolveAsync(ResolutionContext ctx, CancellationToken ct)
    {
        return Task.FromResult(
            ctx.UserSuppliedStatics.GetValueOrDefault("ADMIN_PASSWORD") ?? string.Empty);
    }
}
