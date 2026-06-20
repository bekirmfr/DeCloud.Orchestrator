# Services/TemplateConstants/

Auto-generated partial-class files containing inline artifact constants
(SHA256 + `data:` URI pairs) for template seeders.

**Do not edit files in this folder manually** — they are regenerated from
source files in [DeCloud.Builds](https://github.com/bekirmfr/DeCloud.Builds).

## Files

| File | Source script | Extends |
|------|--------------|---------|
| `SystemVmTemplateSeeder.Artifacts.cs` | `system-vms/compute-artifact-constants.sh` | `SystemVmTemplateSeeder` |
| `TenantVmTemplateSeeder.Artifacts.cs` | `tenant-vms/compute-artifact-constants.sh` | `TenantVmTemplateSeeder` |

## Regeneration workflow

When a script or dashboard file changes in `DeCloud.Builds`:

```bash
# System VM assets changed:
cd DeCloud.Builds/system-vms
bash compute-artifact-constants.sh
# → auto-copies SystemVmTemplateSeeder.Artifacts.cs here

# Tenant VM assets changed:
cd DeCloud.Builds/tenant-vms
bash compute-artifact-constants.sh
# → auto-copies TenantVmTemplateSeeder.Artifacts.cs here
```

Then in the Orchestrator repo:
1. Bump the affected `TemplateRevision` constant in the seeder's main `.cs` file.
2. Commit both the regenerated `.Artifacts.cs` and the revision bump together.

The scripts detect the Orchestrator sibling repo automatically. If the repo
isn't at the expected path, they print a manual copy command instead.

## How it works

Each generated file declares a `partial class` with the same namespace and
class name as its corresponding seeder. The .NET SDK auto-compiles all `.cs`
files in the project — no `.csproj` edits needed. The constants remain
`private`, accessible only to the seeder's `BuildArtifacts()` method.