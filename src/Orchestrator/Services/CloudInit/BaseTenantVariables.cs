using DeCloud.Shared.Models;

namespace DeCloud.Orchestrator.Services.CloudInit;

/// <summary>
/// The <c>base-tenant.yaml</c> placeholder variable declarations — the single
/// source of truth shared by the tenant template seeder and the authored-template
/// compose path.
///
/// <para>
/// Every <c>__VARNAME__</c> that appears in <c>base-tenant.yaml</c> MUST have a
/// declaration here, because <see cref="CloudInitRenderer"/> Pass-1 resolves only
/// the placeholders for variables the template *declares* — it does not scan tokens
/// or walk the resolver registry. A composed template (seeded or authored) therefore
/// needs this set attached to its <c>Variables</c> or the base placeholders render
/// literally. This list is a contract with <c>base-tenant.yaml</c>: if a new
/// <c>__VARNAME__</c> is added to the base, add it here (and to the resolver
/// registry) or renders break.
/// </para>
/// </summary>
public static class BaseTenantVariables
{
    /// <summary>
    /// A fresh list of the base-tenant variable declarations. Returns a new list on
    /// each call so callers may merge template-specific variables into it without
    /// mutating shared state.
    /// </summary>
    public static List<TemplateVariable> Build() => new()
    {
        // Identity (resolved from ctx.Vm)
        new() { Name = "VM_ID",       Kind = VariableKind.Static, Required = true,
                Description = "VM unique identifier (UUID)." },
        new() { Name = "VM_NAME",     Kind = VariableKind.Static, Required = true,
                Description = "VM display name." },
        new() { Name = "HOSTNAME",    Kind = VariableKind.Static, Required = true,
                Description = "Linux hostname for the VM (currently equals VM_NAME)." },

        // Platform context (resolved from ctx.OrchestratorUrl, ctx.Node)
        new() { Name = "ORCHESTRATOR_URL", Kind = VariableKind.Static, Required = true,
                Description = "URL the VM uses to reach the orchestrator." },

        // SSH / password block (resolved from ctx.Vm.Spec.SshPublicKey, UserSuppliedStatics)
        new() { Name = "CA_PUBLIC_KEY", Kind = VariableKind.Static, Required = true,
                Description = "SSH certificate authority public key." },
        new() { Name = "SSH_AUTHORIZED_KEYS_BLOCK", Kind = VariableKind.Static,
                DefaultValue = "# No SSH keys provided",
                Description =
                    "YAML chunk listing user SSH public keys. Empty when neither " +
                    "the VM spec nor user input provided keys." },
        new() { Name = "PASSWORD_CONFIG_BLOCK", Kind = VariableKind.Static,
                DefaultValue = "# No password authentication",
                Description =
                    "YAML chunk for chpasswd.users (cloud-init 22.3+ format). " +
                    "Empty when no admin password is set." },
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
    };
}
