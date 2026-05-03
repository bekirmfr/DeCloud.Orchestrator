using DeCloud.Orchestrator.Interfaces.CloudInit;
using DeCloud.Shared.Models;

namespace DeCloud.Orchestrator.Services.CloudInit.Resolvers.PlatformCommon;

/// <summary>
/// Resolves <c>__CA_PUBLIC_KEY__</c> — SSH certificate authority public key.
/// Source: <c>ctx.Node.SshCaPublicKey</c> (added in P1.9; orchestrator stamps
/// the value at node registration from the node-side <c>/etc/ssh/decloud_ca.pub</c> file).
///
/// <para>
/// Uses <see cref="CloudInitFormatting.IndentForYaml"/> with 6-space indent —
/// matches the placeholder position in <c>base-tenant.yaml</c>:
/// </para>
/// <code>
///     content: |
///       __CA_PUBLIC_KEY__
/// </code>
///
/// <para>
/// If the placeholder column changes in the template, update the indent
/// argument here. See §2 P1.5 decision-log entry for the multi-line indent
/// semantics (line 1 bare, lines 2+ indented).
/// </para>
/// </summary>
public sealed class CaPublicKeyResolver : IVariableResolver
{
    private const int CaKeyIndentSpaces = 6;

    public string ResolverKey => "CA_PUBLIC_KEY";
    public VariableKind Kind => VariableKind.Static;

    public Task<string> ResolveAsync(ResolutionContext ctx, CancellationToken ct)
    {
        var caKey = ctx.Node.SshCaPublicKey;
        if (string.IsNullOrWhiteSpace(caKey))
            throw new InvalidOperationException(
                $"CA_PUBLIC_KEY resolver: ctx.Node.SshCaPublicKey is empty for node {ctx.Node.Id}. " +
                "P1.9 must have stamped this value at node registration. " +
                "Without a CA key, SSH certificate auth into this VM will not work.");

        return Task.FromResult(CloudInitFormatting.IndentForYaml(caKey, CaKeyIndentSpaces));
    }
}
