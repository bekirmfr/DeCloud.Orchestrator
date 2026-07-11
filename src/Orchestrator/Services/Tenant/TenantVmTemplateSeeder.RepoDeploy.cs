using DeCloud.Orchestrator.Services;
using DeCloud.Shared.Enums;
using DeCloud.Shared.Models;
using Orchestrator.Models;

namespace Orchestrator.Services.Tenant;

/// <summary>
/// Seeds the <c>platform-repo-deploy</c> tenant VM template.
///
/// <para>
/// <b>Contract:</b> the user supplies a source URL and the port their app
/// listens on; the VM builds it (Dockerfile → compose → Nixpacks, fixed
/// precedence) and exposes it on port 80. One app, one port, one-shot —
/// for a control plane (rebuilds on push, multiple apps, dashboard),
/// the Coolify template is the answer, and this template's description
/// says so.
/// </para>
///
/// <para>
/// <b>Security shape:</b> all user-controlled strings reach the composed
/// cloud-init as exactly three base64 placeholders, each landing in a
/// <c>write_files</c> entry with <c>encoding: b64</c> — no user byte is
/// ever parsed as YAML, and the orchestrator never touches the repository
/// URL (the clone runs in the guest; no orchestrator SSRF surface).
/// The base64 encoding happens client-side in <c>repo-deploy.js</c>;
/// this seeder declares the placeholders, nothing more. The rendered
/// cloud-init is readable by the hosting node operator — the deploy form
/// discloses this, which is why private repos are limited to read-only
/// single-repo deploy keys (deleted in-guest after clone).
/// </para>
///
/// <para>
/// <b>Wiring:</b> add to <c>SeedAsync</c> alongside the other tenant
/// templates:
/// <code>
/// await SeedTemplateAsync(await BuildRepoDeployTemplateAsync(ct), ct);
/// </code>
/// Update path: edit <c>tenant-vms/repo-deploy/cloud-init.yaml</c> in
/// DeCloud.Builds, then bump <see cref="RepoDeployTemplateRevision"/>.
/// No artifacts — the provision script and status page live in the role
/// layer's write_files (cloud-init content, delivered over the same
/// trusted path; the artifact pipeline is for files a service serves at
/// runtime, which these are not).
/// </para>
/// </summary>
public sealed partial class TenantVmTemplateSeeder
{
    private const string RepoDeployRoleUrl =
        $"{CloudInitRawBase}/tenant-vms/repo-deploy/cloud-init.yaml";

    /// <summary>
    /// Bump when the role layer or this seeder's metadata changes in a way
    /// that should affect new deployments. Running VMs are not redeployed.
    /// </summary>
    private const int RepoDeployTemplateRevision = 1;

    private async Task<VmTemplate> BuildRepoDeployTemplateAsync(CancellationToken ct)
    {
        var baseLayer = await FetchAsync(BaseTenantUrl, ct);
        var roleLayer = await FetchAsync(RepoDeployRoleUrl, ct);

        var composed = TemplateComposer.Compose(
            baseLayer, roleLayer,
            baseName: "base-tenant.yaml",
            roleName: "tenant-vms/repo-deploy/cloud-init.yaml");

        return new VmTemplate
        {
            Slug = "platform-repo-deploy",
            Name = "Deploy from Repository",
            Description =
                "Give it a repository URL and the port your app listens on — " +
                "it builds your code (Dockerfile, docker compose, or automatic " +
                "detection via Nixpacks) and serves it at your VM's address.",
            LongDescription =
                "One-shot deploy from source. Build detection is automatic with " +
                "fixed precedence: a Dockerfile in your repo wins, then a compose " +
                "file (which must publish port 80 itself), then Nixpacks " +
                "(Node, Python, Go, Rust, Ruby, PHP, Java and more — Procfiles " +
                "are honored, and $PORT is set for you). If detection guesses " +
                "wrong, add a Dockerfile.\n\n" +
                "What this template deliberately does NOT do: rebuild when you " +
                "push, host a second app, or provide a dashboard. If you want " +
                "those, deploy the Coolify template instead — it is a full PaaS " +
                "control plane; this is a one-shot.\n\n" +
                "Private repositories: use a read-only, single-repo deploy key. " +
                "It is visible to the node operator hosting your VM and is " +
                "deleted from the VM after cloning — revoke it after deploy. " +
                "Use the Deploy from Repo form for the streamlined experience; " +
                "this template's raw variables are base64-encoded and meant to " +
                "be filled by that form.",
            Version = "1.0.0",
            Revision = RepoDeployTemplateRevision,
            Category = "dev-tools",
            Tags = new List<string> { "git", "deploy", "nixpacks", "docker", "paas" },
            AuthorId = "system",
            AuthorName = "DeCloud",
            IsCommunity = false,
            IsVerified = true,
            Status = TemplateStatus.Published,
            Visibility = TemplateVisibility.Public,
            PricingModel = TemplatePricingModel.Free,
            CloudInitTemplate = composed,

            // Builds are CPU-, memory-, and disk-hungry (Nixpacks compiles
            // from source; docker images multiply). Honest floor, not the
            // platform default — under-provisioned disk is the most common
            // silent build killer.
            MinimumSpec = new VmSpec
            {
                VirtualCpuCores = 2,
                MemoryBytes = 4L * 1024 * 1024 * 1024,
                DiskBytes = 25L * 1024 * 1024 * 1024,
                QualityTier = QualityTier.Standard,
            },
            RecommendedSpec = new VmSpec
            {
                VirtualCpuCores = 2,
                MemoryBytes = 4L * 1024 * 1024 * 1024,
                DiskBytes = 40L * 1024 * 1024 * 1024,
                QualityTier = QualityTier.Standard,
            },

            // Port 80 is the only exposed port, ever, by design: the app is
            // mapped to it (-p 80:$APP_PORT), which keeps this template
            // inside GenericProxyController's InfrastructurePorts and gives
            // BuildServiceList an honest readiness target (the status page
            // answers 503 until the app owns the port). The moment a second
            // port is wanted, the answer is Coolify, not port machinery here.
            ExposedPorts = new List<TemplatePort>
            {
                new() { Port = 80, IsPublic = true },
            },

            Variables = BuildRepoDeployVariables(),
            Artifacts = new List<TemplateArtifact>(),
        };
    }

    /// <summary>
    /// Platform statics (identical set to the other tenant roles) plus
    /// DECLOUD_DOMAIN (resolver-bound, hidden from the deploy form) plus
    /// the three base64-armored user payloads.
    ///
    /// SOURCE_URL, SOURCE_REF, APP_PORT and DATABASE are deliberately NOT
    /// declared variables: they live inside DEPLOY_CONF_B64, shell-quoted
    /// and base64-encoded client-side, so no raw user string ever appears
    /// as a substitution into the YAML body. The b64 variables are the
    /// injection boundary — everything user-typed is behind it.
    /// </summary>
    private static List<TemplateVariable> BuildRepoDeployVariables() => new()
    {
        // Identity (resolved from ctx.Vm)
        new() { Name = "VM_ID",       Kind = VariableKind.Static, Required = true,
                Description = "VM unique identifier (UUID)." },
        new() { Name = "VM_NAME",     Kind = VariableKind.Static, Required = true,
                Description = "VM display name." },
        new() { Name = "HOSTNAME",    Kind = VariableKind.Static, Required = true,
                Description = "Linux hostname for the VM (currently equals VM_NAME)." },

        // Platform context
        new() { Name = "ORCHESTRATOR_URL", Kind = VariableKind.Static, Required = true,
                Description = "URL the VM uses to reach the orchestrator." },

        // SSH / password
        new() { Name = "CA_PUBLIC_KEY", Kind = VariableKind.Static, Required = true,
                Description = "SSH certificate authority public key." },
        new() { Name = "SSH_AUTHORIZED_KEYS_BLOCK", Kind = VariableKind.Static,
                DefaultValue = "# No SSH keys provided",
                Description = "YAML chunk listing user SSH public keys." },
        new() { Name = "PASSWORD_CONFIG_BLOCK", Kind = VariableKind.Static,
                DefaultValue = "# No password authentication",
                Description = "YAML chunk for chpasswd.users (cloud-init 22.3+ format)." },
        new() { Name = "ADMIN_PASSWORD", Kind = VariableKind.Static,
                DefaultValue = "",
                Description =
                    "Plaintext root password. Set via UserSuppliedStatics " +
                    "[\"ADMIN_PASSWORD\"] at deploy time. Empty for SSH-only deploys." },
        new() { Name = "SSH_PASSWORD_AUTH", Kind = VariableKind.Static,
                DefaultValue = "false",
                Description =
                    "'true' or 'false' string for cloud-init's ssh_pwauth. " +
                    "Derived from ADMIN_PASSWORD presence." },

        // Resolver-bound (DeCloudDomainResolver) — hidden from the deploy
        // form because the platform fills it. Used in final_message.
        new() { Name = "DECLOUD_DOMAIN", Kind = VariableKind.Static, Required = true,
                Description = "The VM's public subdomain." },

        // ── User payloads (base64-armored, built by repo-deploy.js) ─────
        new() { Name = "DEPLOY_CONF_B64", Kind = VariableKind.Static, Required = true,
                Description =
                    "Base64 of shell-quoted KEY='value' lines: SOURCE_URL " +
                    "(required), SOURCE_REF, APP_PORT, DATABASE. Built by the " +
                    "Deploy from Repo form — advanced users deploying via the " +
                    "generic form must base64-encode by hand." },
        new() { Name = "APP_ENV_B64", Kind = VariableKind.Static,
                DefaultValue = "IyBkZWNsb3VkOiBubyB1c2VyIGVudg==", // "# decloud: no user env"
                Description =
                    "Base64 of KEY=value lines delivered to the app via " +
                    "docker --env-file. Visible to the node operator hosting " +
                    "the VM (cloud-init transport)." },
        new() { Name = "DEPLOY_KEY_B64", Kind = VariableKind.Static,
                DefaultValue = "", // empty file → public clone path
                Description =
                    "Base64 of a read-only single-repo SSH deploy key for " +
                    "private clones. Deleted in-guest after cloning. Visible " +
                    "to the node operator — revoke after deploy." },
    };
}
