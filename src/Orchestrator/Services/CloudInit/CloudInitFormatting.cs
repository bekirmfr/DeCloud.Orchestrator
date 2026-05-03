using System;
using System.Collections.Generic;
using System.Text;

namespace DeCloud.Orchestrator.Services.CloudInit;

/// <summary>
/// Pure-function helpers that produce the YAML chunks substituted into
/// cloud-init placeholders. Lifted from <c>LibvirtVmManager.CreateCloudInitIsoAsync</c>
/// (steps 2, 3, 5) so the new <see cref="CloudInitRenderer"/> and the legacy
/// <c>VmService</c> path can share the same logic without copy-paste drift.
///
/// <para>
/// All methods are pure: no I/O, no static state, no DI dependencies.
/// Caller decides when to invoke (e.g., system VMs of type Relay don't get
/// SSH keys or passwords; that's a caller-side guard, not a formatter concern).
/// </para>
/// </summary>
public static class CloudInitFormatting
{
    /// <summary>
    /// Builds the YAML chunk that substitutes for <c>__SSH_AUTHORIZED_KEYS_BLOCK__</c>.
    ///
    /// <para>
    /// Output for one key <c>"ssh-rsa AAAA... user@host"</c>:
    /// </para>
    /// <code>
    /// ssh_authorized_keys:
    ///   - ssh-rsa AAAA... user@host
    /// </code>
    ///
    /// <para>
    /// When no keys are provided, returns the comment string <c># No SSH keys provided</c>.
    /// This is intentional: the placeholder must always be replaced with valid YAML
    /// (an empty string would leave a blank line that may or may not parse depending on
    /// the surrounding context). A comment is always valid.
    /// </para>
    /// </summary>
    /// <param name="keys">Zero or more SSH public keys, one per element.</param>
    public static string BuildSshKeysBlock(IEnumerable<string>? keys)
    {
        if (keys is null) return "# No SSH keys provided";

        var trimmed = new List<string>();
        foreach (var k in keys)
        {
            if (string.IsNullOrWhiteSpace(k)) continue;
            trimmed.Add(k.Trim());
        }

        if (trimmed.Count == 0) return "# No SSH keys provided";

        var sb = new StringBuilder();
        sb.AppendLine("ssh_authorized_keys:");
        foreach (var key in trimmed)
            sb.AppendLine($"  - {key}");

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Convenience overload for callers that hold SSH keys as a single
    /// newline-separated string (matches the existing
    /// <c>VmSpec.SshPublicKey</c> shape).
    /// </summary>
    public static string BuildSshKeysBlock(string? newlineSeparatedKeys)
    {
        if (string.IsNullOrEmpty(newlineSeparatedKeys))
            return "# No SSH keys provided";

        return BuildSshKeysBlock(
            newlineSeparatedKeys.Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// Builds the YAML chunk that substitutes for <c>__PASSWORD_CONFIG_BLOCK__</c>.
    /// Tenant VMs only — system VMs (relay, dht, blockstore) don't receive passwords.
    ///
    /// <para>
    /// Uses the <c>chpasswd.users</c> format (cloud-init 22.3+, Ubuntu 22.04 and 24.04).
    /// The older <c>chpasswd.list</c> format was deprecated in cloud-init 23.x and
    /// silently fails on Ubuntu 24.04 (cloud-init 24.x), leaving root with no password
    /// and breaking console + SSH password login. <b>Do not change this format
    /// without coordinating with the Ubuntu image we ship.</b>
    /// </para>
    ///
    /// <para>
    /// Output for password <c>"hunter2"</c>:
    /// </para>
    /// <code>
    /// chpasswd:
    ///   users:
    ///     - name: root
    ///       password: "hunter2"
    ///       type: text
    ///   expire: false
    /// </code>
    ///
    /// <para>
    /// When no password is provided, returns <c># No password authentication</c>
    /// (same intent as <see cref="BuildSshKeysBlock"/> — placeholder must be
    /// replaced with valid YAML).
    /// </para>
    ///
    /// <para>
    /// <b>Plaintext passwords:</b> The password is emitted in plaintext into the
    /// cloud-init document (with <c>type: text</c>). Cloud-init then hashes it
    /// internally before applying. The cloud-init document itself is regarded as
    /// secret material — same trust boundary as the SSH keys it carries. This
    /// matches existing production behavior; no change in posture.
    /// </para>
    /// </summary>
    /// <param name="password">The plaintext root password, or null/empty for none.</param>
    public static string BuildPasswordBlock(string? password)
    {
        if (string.IsNullOrEmpty(password))
            return "# No password authentication";

        var sb = new StringBuilder();
        sb.AppendLine("chpasswd:");
        sb.AppendLine("  users:");
        sb.AppendLine("    - name: root");
        sb.AppendLine($"      password: \"{password}\"");
        sb.AppendLine("      type: text");
        sb.AppendLine("  expire: false");

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Indents multi-line content so that, when substituted into a placeholder
    /// already at column <paramref name="spaces"/>, every line ends up at that
    /// same column.
    ///
    /// <para>
    /// <b>Semantics:</b> The first line is returned bare (no leading indent),
    /// because the placeholder's own column position provides line 1's indent.
    /// Subsequent lines are prefixed with <paramref name="spaces"/> spaces so
    /// they line up under line 1. Each line is also trimmed of surrounding
    /// whitespace before re-indenting, normalizing input that may carry its
    /// own quirks.
    /// </para>
    ///
    /// <para>
    /// Example: placeholder at column 6, content
    /// <c>"line1\nline2\nline3"</c>, <c>spaces = 6</c>:
    /// <code>
    /// // template:
    /// //       __PLACEHOLDER__
    /// // helper returns:
    /// //   "line1\n      line2\n      line3"
    /// // after string.Replace, document reads:
    /// //       line1
    /// //       line2
    /// //       line3
    /// </code>
    /// </para>
    ///
    /// <para>
    /// <b>Behavior change vs. lifted source:</b> The original
    /// <c>LibvirtVmManager</c> code prefixed <i>every</i> line with the indent
    /// (including the first), producing asymmetric output after substitution
    /// (line 1 at column 12, line 2+ at column 6). YAML auto-detection masked
    /// this for single-line CA keys (the common case) but breaks block scalars
    /// for multi-line content. The new helper indents only lines 2+, which is
    /// correct for both single-line and multi-line inputs. Documented as a
    /// deliberate fix in §2 of the implementation plan.
    /// </para>
    /// </summary>
    /// <param name="content">Multi-line content. May be null/empty (returns "").</param>
    /// <param name="spaces">Indent for lines after the first. Must be ≥ 0.</param>
    public static string IndentForYaml(string? content, int spaces)
    {
        if (spaces < 0)
            throw new ArgumentOutOfRangeException(nameof(spaces),
                "Indent must be non-negative.");

        if (string.IsNullOrEmpty(content))
            return string.Empty;

        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0)
            return string.Empty;

        var indent = new string(' ', spaces);
        var sb = new StringBuilder();

        // First line: bare — placeholder column provides indent.
        sb.Append(lines[0].Trim());

        // Subsequent lines: prefix with `spaces` so they align with line 1.
        for (var i = 1; i < lines.Length; i++)
        {
            sb.Append('\n');
            sb.Append(indent);
            sb.Append(lines[i].Trim());
        }

        return sb.ToString();
    }
}
