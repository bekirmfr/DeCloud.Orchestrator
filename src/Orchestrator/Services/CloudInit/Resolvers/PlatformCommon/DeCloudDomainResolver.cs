using DeCloud.Orchestrator.Interfaces.CloudInit;
using DeCloud.Shared.Models;
using Orchestrator.Services;

namespace DeCloud.Orchestrator.Services.CloudInit.Resolvers.PlatformCommon;

/// <summary>
/// Resolves <c>__DECLOUD_DOMAIN__</c> to the default subdomain assigned to a
/// tenant VM by central ingress (e.g. <c>ai-chatbot-0887.vms.stackfi.tech</c>).
///
/// <para>
/// Mirrors <c>TemplateService.GetAvailableVariables</c>'s computation for the
/// same key. Registering this as a platform resolver — rather than relying on
/// the <c>UserSuppliedStatics</c> fallback path the legacy <c>${VARNAME}</c>
/// substitution used — makes the variable platform-bound per design §2.4 so
/// the marketplace deploy form correctly hides the field.
/// </para>
///
/// <para>
/// System VMs must never declare <c>DECLOUD_DOMAIN</c>; this resolver throws
/// if invoked without a tenant VM in the resolution context.
/// </para>
/// </summary>
public sealed class DeCloudDomainResolver : IVariableResolver
{
    private readonly ICentralIngressService _ingressService;

    public DeCloudDomainResolver(ICentralIngressService ingressService)
    {
        _ingressService = ingressService;
    }

    public string ResolverKey => "DECLOUD_DOMAIN";
    public VariableKind Kind => VariableKind.Static;

    public Task<string> ResolveAsync(ResolutionContext ctx, CancellationToken ct)
    {
        if (ctx.Vm is null)
            throw new InvalidOperationException(
                "DeCloudDomainResolver requires a tenant VM context (ctx.Vm is null). " +
                "System VMs should not declare DECLOUD_DOMAIN.");

        var baseDomain = _ingressService.BaseDomain ?? "vms.decloud.app";
        var domain = ctx.Vm.IngressConfig?.DefaultSubdomain
                     ?? $"{ctx.Vm.Id}.{baseDomain}";

        return Task.FromResult(domain);
    }
}