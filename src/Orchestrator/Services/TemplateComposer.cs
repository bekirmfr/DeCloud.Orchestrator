using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace DeCloud.Orchestrator.Services;

/// <summary>
/// Merges a base cloud-init layer with a role-specific layer into a single
/// composed cloud-init document.
///
/// <para>
/// This service is the bridge between the multi-file source-of-truth in
/// <c>DeCloud.Builds/</c> (base-templates/ + role/cloud-init.yaml) and the
/// single-document <c>VmTemplate.CloudInitTemplate</c> stored in MongoDB.
/// <see cref="TemplateSeederService"/> calls <see cref="Compose"/> at startup
/// to produce the composed document for each role.
/// </para>
///
/// <para>
/// <b>Composition rules:</b>
/// <list type="bullet">
///   <item><b>Scalars</b> (hostname, manage_etc_hosts, package_update,
///     package_upgrade, disable_root, ssh_pwauth, final_message): role wins
///     if present; base wins otherwise.</item>
///   <item><b>packages</b>: union with deduplication. Base order preserved;
///     role-only items appended.</item>
///   <item><b>bootcmd, runcmd, write_files</b>: concatenation. Base items
///     emitted first, then role items.</item>
///   <item><b>Top-level placeholders</b> (e.g., <c>__SSH_AUTHORIZED_KEYS_BLOCK__</c>):
///     preserved verbatim at their position in the base file's source order.
///     <see cref="CloudInitRenderer"/> substitutes them at render time.</item>
/// </list>
/// </para>
///
/// <para>
/// <b>String-based, not parser-based.</b> Both inputs MUST follow the conventions
/// of files in <c>DeCloud.Builds/</c>: top-level keys at column 0, list items
/// at 2-space indent, multi-line strings via <c>|</c> block scalar, comments
/// via <c>#</c>. A YAML parser dependency is intentionally avoided — round-trip
/// through a parser risks mangling multi-line strings, comments, and the
/// <c>__PLACEHOLDER__</c> markers.
/// </para>
///
/// <para>
/// <b>Known characteristic:</b> Comments that sit BETWEEN sections in the source
/// file travel with the PRECEDING section's content slab in the output. This is
/// because a comment line (column 0, starting with <c>#</c>) is treated as
/// continuation of the open section, not as a free-floating element. In practice
/// this is acceptable; the alternative (heuristics for "comments preceding the
/// next section") adds parser complexity for marginal gain.
/// </para>
/// </summary>
public static class TemplateComposer
{
    // Regex for column-0 lines that introduce a top-level YAML key.
    private static readonly Regex KeyRegex = new(
        @"^([a-zA-Z_][a-zA-Z0-9_]*):(.*)$",
        RegexOptions.Compiled);

    // Regex for column-0 lines that are __PLACEHOLDER__ markers (no key suffix).
    private static readonly Regex PlaceholderRegex = new(
        @"^(__[A-Z0-9_]+__)\s*$",
        RegexOptions.Compiled);

    private enum MergeMode
    {
        ScalarRoleWins,
        ListConcat,
        ListUnion,
    }

    private static readonly Dictionary<string, MergeMode> Modes = new(StringComparer.Ordinal)
    {
        ["hostname"] = MergeMode.ScalarRoleWins,
        ["manage_etc_hosts"] = MergeMode.ScalarRoleWins,
        ["package_update"] = MergeMode.ScalarRoleWins,
        ["package_upgrade"] = MergeMode.ScalarRoleWins,
        ["disable_root"] = MergeMode.ScalarRoleWins,
        ["ssh_pwauth"] = MergeMode.ScalarRoleWins,
        ["final_message"] = MergeMode.ScalarRoleWins,
        ["packages"] = MergeMode.ListUnion,
        ["bootcmd"] = MergeMode.ListConcat,
        ["write_files"] = MergeMode.ListConcat,
        ["runcmd"] = MergeMode.ListConcat,
    };

    /// <summary>
    /// Merges <paramref name="baseLayer"/> and <paramref name="roleLayer"/> into
    /// a single composed cloud-init document.
    /// </summary>
    /// <param name="baseLayer">Verbatim contents of base-{system,system-mesh,tenant}.yaml.</param>
    /// <param name="roleLayer">Verbatim contents of {role}/cloud-init.yaml.</param>
    /// <param name="baseName">Display name of the base file (for the generated header).</param>
    /// <param name="roleName">Display name of the role file (for the generated header).</param>
    /// <returns>The composed cloud-init document, terminated with a single newline.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown if either input has a column-0 line that is neither a recognized
    /// key (<c>identifier:</c>) nor a placeholder (<c>__NAME__</c>).
    /// </exception>
    public static string Compose(string baseLayer, string roleLayer, string baseName, string roleName)
    {
        var baseSections = ParseSections(baseLayer);
        var roleSections = ParseSections(roleLayer);

        // Index role sections by key for quick lookup.
        var roleByKey = roleSections
            .Where(s => s.Kind == SectionKind.Key)
            .ToDictionary(s => s.Key!, StringComparer.Ordinal);
        var consumedRoleKeys = new HashSet<string>(StringComparer.Ordinal);

        var sb = new StringBuilder();
        sb.AppendLine("#cloud-config");
        sb.AppendLine("# Composed by TemplateComposer from:");
        sb.AppendLine($"#   base: {baseName}");
        sb.AppendLine($"#   role: {roleName}");
        sb.AppendLine($"# Generated at: {DateTimeOffset.UtcNow:yyyy-MM-ddTHH:mm:ssZ}");
        sb.AppendLine("# DO NOT EDIT — regenerate by running TemplateSeederService.");
        sb.AppendLine();

        // Walk base sections in source order. Emit each merged with its role counterpart.
        foreach (var bs in baseSections)
        {
            if (bs.Kind == SectionKind.Placeholder)
            {
                sb.AppendLine(bs.RawText);
                sb.AppendLine();
                continue;
            }

            var key = bs.Key!;
            roleByKey.TryGetValue(key, out var rs);
            if (rs is not null)
                consumedRoleKeys.Add(key);

            var mode = Modes.TryGetValue(key, out var m) ? m : MergeMode.ScalarRoleWins;
            EmitMerged(sb, bs, rs, key, mode);
        }

        // Emit role-only keys (keys present in role but not base).
        foreach (var rs in roleSections.Where(s => s.Kind == SectionKind.Key))
        {
            if (consumedRoleKeys.Contains(rs.Key!))
                continue;
            var mode = Modes.TryGetValue(rs.Key!, out var m) ? m : MergeMode.ScalarRoleWins;
            EmitMerged(sb, baseSection: null, rs, rs.Key!, mode);
        }

        return TrimTrailingBlankLines(sb.ToString()) + "\n";
    }

    private enum SectionKind { Key, Placeholder }

    private sealed record Section(
        SectionKind Kind,
        string? Key,
        string? InlineValue,
        IReadOnlyList<string> BlockLines,
        string? RawText);

    /// <summary>
    /// Parse a YAML document into ordered sections (key sections + placeholder
    /// markers). Comments and blank lines preceding the first section are
    /// dropped (the composer regenerates the file header).
    /// </summary>
    private static List<Section> ParseSections(string yaml)
    {
        var sections = new List<Section>();
        var lines = yaml.Replace("\r\n", "\n").Split('\n');

        Section? current = null;
        var blockLines = new List<string>();

        void Flush()
        {
            if (current is null) return;
            sections.Add(current with { BlockLines = blockLines.ToArray() });
            blockLines = new List<string>();
            current = null;
        }

        foreach (var rawLine in lines)
        {
            var line = rawLine;

            // Drop leading file-level comments / blanks before any section opens.
            if (current is null &&
                (string.IsNullOrWhiteSpace(line) || line.StartsWith('#')))
                continue;

            // Detect a column-0 line that opens a new section.
            if (line.Length > 0 && line[0] != ' ' && line[0] != '\t' && line[0] != '#')
            {
                var keyMatch = KeyRegex.Match(line);
                if (keyMatch.Success)
                {
                    Flush();
                    var inline = keyMatch.Groups[2].Value.Trim();
                    current = new Section(
                        Kind: SectionKind.Key,
                        Key: keyMatch.Groups[1].Value,
                        InlineValue: string.IsNullOrEmpty(inline) ? null : inline,
                        BlockLines: Array.Empty<string>(),
                        RawText: null);
                    continue;
                }

                var phMatch = PlaceholderRegex.Match(line);
                if (phMatch.Success)
                {
                    Flush();
                    sections.Add(new Section(
                        Kind: SectionKind.Placeholder,
                        Key: null,
                        InlineValue: null,
                        BlockLines: Array.Empty<string>(),
                        RawText: line));
                    continue;
                }

                throw new InvalidOperationException(
                    $"Unrecognized column-0 line (not a key or placeholder): {line}");
            }

            // Continuation: belongs to the open section's block content.
            if (current is not null)
                blockLines.Add(line);
        }

        Flush();
        return sections;
    }

    private static void EmitMerged(
        StringBuilder sb,
        Section? baseSection,
        Section? roleSection,
        string key,
        MergeMode mode)
    {
        switch (mode)
        {
            case MergeMode.ScalarRoleWins:
                EmitScalar(sb, roleSection ?? baseSection);
                break;
            case MergeMode.ListConcat:
                EmitListConcat(sb, baseSection, roleSection, key);
                break;
            case MergeMode.ListUnion:
                EmitListUnion(sb, baseSection, roleSection, key);
                break;
        }
    }

    private static void EmitScalar(StringBuilder sb, Section? section)
    {
        if (section is null) return;
        var key = section.Key!;
        if (section.InlineValue is not null)
        {
            sb.Append(key).Append(": ").AppendLine(section.InlineValue);
        }
        else if (section.BlockLines.Count > 0)
        {
            sb.Append(key).AppendLine(":");
            foreach (var line in TrimTrailingBlanks(section.BlockLines))
                sb.AppendLine(line);
        }
        else
        {
            // Empty section: just the bare key (rare but possible).
            sb.Append(key).AppendLine(":");
        }
        sb.AppendLine();
    }

    private static void EmitListConcat(
        StringBuilder sb, Section? baseSection, Section? roleSection, string key)
    {
        if (baseSection is null && roleSection is null) return;
        sb.Append(key).AppendLine(":");
        if (baseSection is not null)
            foreach (var line in TrimTrailingBlanks(baseSection.BlockLines))
                sb.AppendLine(line);
        if (roleSection is not null)
            foreach (var line in TrimTrailingBlanks(roleSection.BlockLines))
                sb.AppendLine(line);
        sb.AppendLine();
    }

    private static void EmitListUnion(
        StringBuilder sb, Section? baseSection, Section? roleSection, string key)
    {
        if (baseSection is null && roleSection is null) return;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ordered = new List<string>();

        foreach (var section in new[] { baseSection, roleSection })
        {
            if (section is null) continue;
            foreach (var line in section.BlockLines)
            {
                var stripped = line.TrimStart();
                if (!stripped.StartsWith("- ", StringComparison.Ordinal)) continue;
                var item = stripped.Substring(2).Trim();
                if (seen.Add(item))
                    ordered.Add(item);
            }
        }

        if (ordered.Count == 0) return;

        sb.Append(key).AppendLine(":");
        foreach (var item in ordered)
            sb.Append("  - ").AppendLine(item);
        sb.AppendLine();
    }

    private static IEnumerable<string> TrimTrailingBlanks(IReadOnlyList<string> lines)
    {
        var end = lines.Count;
        while (end > 0 && string.IsNullOrWhiteSpace(lines[end - 1])) end--;
        for (var i = 0; i < end; i++) yield return lines[i];
    }

    private static string TrimTrailingBlankLines(string input)
    {
        var trimmed = input.TrimEnd('\r', '\n', ' ', '\t');
        return trimmed;
    }
}
