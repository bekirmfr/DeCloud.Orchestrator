using DeCloud.Orchestrator.Interfaces.CloudInit;
using DeCloud.Shared.Models;

namespace DeCloud.Orchestrator.Services.CloudInit.Resolvers.PlatformCommon;

/// <summary>
/// Resolves <c>__SSH_AUTHORIZED_KEYS_BLOCK__</c> — the YAML chunk listing
/// SSH public keys authorized for password-less login.
///
/// <para>
/// Source priority:
/// </para>
/// <list type="number">
///   <item><c>ctx.Vm.SshPublicKey</c> — tenant flow (newline-separated).</item>
///   <item><c>ctx.UserSuppliedStatics["SSH_PUBLIC_KEYS"]</c> — operator override
///     (e.g., emergency access on system VMs).</item>
///   <item>Empty — produces <c># No SSH keys provided</c>.</item>
/// </list>
///
/// <para>
/// System VMs (DHT, BlockStore, Relay) typically use SSH CA-signed certificates
/// only and reach this resolver with no keys; the empty-marker output is the
/// expected path for them.
/// </para>
/// </summary>
public sealed class SshAuthorizedKeysBlockResolver : IVariableResolver
{
    public string ResolverKey => "SSH_AUTHORIZED_KEYS_BLOCK";
    public VariableKind Kind => VariableKind.Static;

    public Task<string> ResolveAsync(ResolutionContext ctx, CancellationToken ct)
    {
        var keys = ctx.Vm?.SshPublicKey
                   ?? ctx.UserSuppliedStatics.GetValueOrDefault("SSH_PUBLIC_KEYS");
        return Task.FromResult(CloudInitFormatting.BuildSshKeysBlock(keys));
    }
}
