# Node Locality: Country, Region, Zone

Reference for how DeCloud models a node's location. Three orthogonal
attributes — **country**, **region**, **zone** — answering three different
questions, each with its own update cadence, source of truth, and
matching semantics.

> **Companion documents:**
> - [`RELEASE-PIPELINE.md`](RELEASE-PIPELINE.md) — how releases are built and signed
> - [`NODE-LIFECYCLE.md`](NODE-LIFECYCLE.md) — how install/update/uninstall flows work

---

## Table of Contents

1. [Why three axes](#why-three-axes)
2. [Country (jurisdiction)](#country-jurisdiction)
3. [Region (network locality)](#region-network-locality)
4. [Zone (operator-scoped)](#zone-operator-scoped)
5. [Reference data files](#reference-data-files)
6. [Data model](#data-model)
7. [API endpoints](#api-endpoints)
8. [Validation rules](#validation-rules)
9. [Scheduling semantics](#scheduling-semantics)
10. [install.sh prompt behavior](#installsh-prompt-behavior)
11. [Worked examples](#worked-examples)
12. [Migration from legacy `default/default`](#migration-from-legacy-defaultdefault)
13. [What is intentionally NOT here](#what-is-intentionally-not-here)
14. [References](#references)

---

## Why three axes

Earlier versions used `Region` and `Zone` as free-form strings doing
triple duty: jurisdiction marker, network-locality bucket, and operator
grouping. This conflates three claims with different correctness
criteria.

Separating them makes each one cleaner:

| Axis | Question it answers | Determined by | Updated when | Source of truth | Matching |
| --- | --- | --- | --- | --- | --- |
| **Country** | Where does this node legally reside? Which laws apply? | Where the machine physically sits | Machine moves countries (rare) | Operator declaration, IP corroboration | Categorical (exact match) |
| **Region** | Where on the global network is this node, roughly? | Network topology + geography | Network path changes (occasional) | Operator declaration, default suggested by country | Similarity (region equality + adjacency graph) |
| **Zone** | Within this region, how should I group nodes? | Operator's choice | Operator reorganizes (rare) | Operator declaration | Categorical, but no independence guarantee |

A tenant filtering for "GDPR-compliant nodes" filters by **country**'s
EU membership tag. A tenant wanting "low-latency to my Frankfurt
endpoint" filters by **region** `eu-central`. A tenant wanting "same
rack as my other VM" filters by **zone**. Three different filters,
three different data shapes.

### Anti-patterns this avoids

- ❌ **`region: "us-east-1"`** doing both "USA" (jurisdiction) and
  "east coast" (locality). Tenants with EU compliance can't filter
  cleanly; "is this region in the EU?" requires parsing.
- ❌ **`region: "eu-west"` for a Brazilian operator's leased
  Frankfurt box** (jurisdiction-BR? jurisdiction-DE?). A single field
  has to lie about something.
- ❌ **`zone` implying failure independence**. Operators run
  multiple nodes in the same physical rack and label them as
  different zones for their own organization; pretending they're
  independent oversells what we can deliver.

---

## Country (jurisdiction)

### Standard

**ISO 3166-1 alpha-2** — the universal two-letter country code list
maintained by the International Organization for Standardization.

```
TR    Türkiye
DE    Germany
US    United States
JP    Japan
ZZ    Unknown / declined / not applicable
```

The full list is ~250 entries. ISO updates it roughly twice a year for
political changes (countries split, rename, codes added/retired).

### Format

- Exactly two uppercase ASCII letters
- Always uppercase: `TR`, never `tr` or `Tr`
- `ZZ` is reserved for "unknown" (operator declined to declare, or
  registration occurred before the country field existed)

### Supranational membership tags

Countries are grouped into supranational blocs that tenants may filter
by — most commonly for regulatory compliance. There is no single open
standard list of memberships; each bloc maintains its own roster. We
compile a mapping table in `countries.json`.

Tags currently tracked:

| Tag | Meaning | Source |
| --- | --- | --- |
| `EU` | European Union member state | europa.eu official list |
| `EEA` | European Economic Area | EU's published EEA list (EU + IS, LI, NO) |
| `Schengen` | Schengen Area participant | EU's Schengen page |
| `EU-CustomsUnion` | EU Customs Union (incl. non-EU members like Türkiye) | EU trade agreements |
| `NATO` | North Atlantic Treaty Organization member | nato.int |
| `CouncilOfEurope` | Council of Europe member (broader than EU) | coe.int |
| `USMCA` | United States-Mexico-Canada Agreement | USTR / SICE |
| `Mercosur` | Southern Common Market | mercosur.int |
| `ASEAN` | Association of Southeast Asian Nations | asean.org |
| `GCC` | Gulf Cooperation Council | gcc-sg.org |
| `AU` | African Union | au.int |
| `CIS` | Commonwealth of Independent States | cis.minsk.by |

This list grows when there is a real driver — a tenant requirement,
a new bloc with broad membership change. Memberships update rarely
(Brexit was the last major edit; UK lost `EU`, `EEA`, `Schengen`).

Supranational tags are **derived server-side** from the country at
registration time. The operator declares country only; tags follow.

### Examples

```json
{ "code": "TR", "name": "Türkiye",       "tags": ["NATO", "CouncilOfEurope", "EU-CustomsUnion"] }
{ "code": "DE", "name": "Germany",       "tags": ["EU", "EEA", "Schengen", "NATO", "CouncilOfEurope"] }
{ "code": "GB", "name": "United Kingdom","tags": ["NATO", "CouncilOfEurope"] }
{ "code": "US", "name": "United States", "tags": ["NATO", "USMCA"] }
{ "code": "JP", "name": "Japan",         "tags": [] }
{ "code": "BR", "name": "Brazil",        "tags": ["Mercosur"] }
```

---

## Region (network locality)

### Standard

**DeCloud-defined enumeration** — there is no fitting open standard.
UN M.49 is statistical (groups Türkiye, Japan, Saudi Arabia together as
"Asia"), RIR regions are too coarse (5 globally), and AWS/Azure/GCP each
invented their own incompatible vocabularies. We adopt their de facto
naming convention but maintain our own list.

### Format

- Lowercase ASCII letters and hyphens only
- Continental prefix: `na`, `sa`, `eu`, `me`, `af`, `apac`, `oceania`
- Optional cardinal direction suffix: `-east`, `-west`, `-north`,
  `-south`, `-central`, `-northeast`, `-southeast`
- Closed list, ~20 values

### The full list

```json
[
  { "code": "na-east",        "name": "North America East",     "continent": "NA" },
  { "code": "na-central",     "name": "North America Central",  "continent": "NA" },
  { "code": "na-west",        "name": "North America West",     "continent": "NA" },
  { "code": "sa-east",        "name": "South America East",     "continent": "SA" },
  { "code": "sa-west",        "name": "South America West",     "continent": "SA" },
  { "code": "eu-west",        "name": "Europe West",            "continent": "EU" },
  { "code": "eu-central",     "name": "Europe Central",         "continent": "EU" },
  { "code": "eu-north",       "name": "Europe North",           "continent": "EU" },
  { "code": "eu-south",       "name": "Europe South",           "continent": "EU" },
  { "code": "eu-east",        "name": "Europe East",            "continent": "EU" },
  { "code": "me-west",        "name": "Middle East West",       "continent": "ME" },
  { "code": "me-east",        "name": "Middle East East",       "continent": "ME" },
  { "code": "af-north",       "name": "Africa North",           "continent": "AF" },
  { "code": "af-south",       "name": "Africa South",           "continent": "AF" },
  { "code": "af-east",        "name": "Africa East",            "continent": "AF" },
  { "code": "apac-northeast", "name": "Asia-Pacific Northeast", "continent": "AS" },
  { "code": "apac-east",      "name": "Asia-Pacific East",      "continent": "AS" },
  { "code": "apac-southeast", "name": "Asia-Pacific Southeast", "continent": "AS" },
  { "code": "apac-south",     "name": "Asia-Pacific South",     "continent": "AS" },
  { "code": "oceania",        "name": "Oceania",                "continent": "OC" }
]
```

New regions are added only when operator density justifies a split. A
new region requires:
- Updating `regions.json`
- Updating the adjacency graph (see below)
- Updating `country-region-defaults.json` if the split changes any
  country's default
- Orchestrator redeploy

This is not an operator-self-service operation by design. Region
proliferation degrades scheduling.

### Adjacency graph

Used for soft locality scoring (see [Scheduling
semantics](#scheduling-semantics)). Hand-curated based on internet
topology and submarine cable routes:

```json
{
  "na-east":        ["na-central", "eu-west"],
  "na-central":     ["na-east", "na-west"],
  "na-west":        ["na-central", "apac-northeast", "oceania"],
  "sa-east":        ["sa-west", "na-east"],
  "sa-west":        ["sa-east", "na-west"],
  "eu-west":        ["eu-central", "eu-north", "na-east"],
  "eu-central":     ["eu-west", "eu-north", "eu-south", "eu-east"],
  "eu-north":       ["eu-west", "eu-central"],
  "eu-south":       ["eu-central", "eu-east", "me-west", "af-north"],
  "eu-east":        ["eu-central", "eu-south", "me-west"],
  "me-west":        ["eu-south", "eu-east", "me-east", "af-north"],
  "me-east":        ["me-west", "apac-south"],
  "af-north":       ["eu-south", "me-west", "af-east"],
  "af-east":        ["af-north", "af-south", "me-west"],
  "af-south":       ["af-east"],
  "apac-northeast": ["apac-east", "na-west"],
  "apac-east":      ["apac-northeast", "apac-southeast"],
  "apac-southeast": ["apac-east", "apac-south", "oceania"],
  "apac-south":     ["apac-southeast", "me-east"],
  "oceania":        ["apac-southeast", "na-west"]
}
```

This is approximate. Latency-critical applications should benchmark.

### Edge cases

Some country → region assignments are not obvious; reasonable engineers
disagree. We make explicit, documented choices.

| Country | Choice | Reasoning |
| --- | --- | --- |
| **TR** (Türkiye) | `eu-east` | Network: Turkish IXPs peer densely with European networks. Cultural/economic: Turkish infrastructure self-positions European. Alternative `me-west` is geographically closer (Tel Aviv ~25ms vs Frankfurt ~35ms) but ME peering is sparser. |
| **RU** (Russia) | `eu-east` | Network topology aligned with Eastern Europe pre-2022; remains so technically despite political tensions. |
| **IL** (Israel) | `me-west` | Self-evident geographically. Some ME tenants exclude IL on geopolitical grounds — they should filter by country, not region. |
| **EG** (Egypt) | `me-west` | Network peering favours ME over Africa despite continental membership. |
| **CY** (Cyprus) | `eu-east` | EU member; Mediterranean cable landings favour European peering over ME. |
| **MX** (Mexico) | `na-central` | Network proximity to US Southwest and Texas. Cultural Latin America notwithstanding. |
| **AU** (Australia) | `oceania` | Distinct submarine cable topology from APAC mainland. |
| **NZ** (New Zealand) | `oceania` | Same as AU. |
| **IS** (Iceland) | `eu-north` | Despite mid-Atlantic position; transatlantic cables land here as part of European routing. |
| **GE** (Georgia), **AM** (Armenia), **AZ** (Azerbaijan) | `eu-east` | Caucasus is ambiguous; we group with Eastern Europe rather than Asia for peering reasons. |

Operators can always override their default with `--region` if their
specific network reality differs.

---

## Zone (operator-scoped)

### Standard

**No external standard.** Zone is a DeCloud convention — a region-prefixed
operator-organizational tag.

### Format

- Must start with the parent region's code, followed by `-`
- Followed by a positive integer
- Pattern: `^<region>-[1-9][0-9]*$`

Examples:
```
eu-central-1
eu-central-2
tr-south-1                                    ← INVALID (region "tr-south" doesn't exist)
us-east-1                                     ← INVALID (region "us-east" doesn't exist; use "na-east-1")
na-east-1                                     ← VALID
```

> **Historical note.** Pre-standard installs may have produced zone
> values like `tr-south-01` or `us-east-1a` (mimicking AWS). These are
> grandfathered as opaque strings; new registrations must conform to
> `<region>-<n>`.

### What zone IS

- An organizational convenience for operators running multiple nodes
- A weak locality hint within a region
- Operator-meaningful (their rack, their datacenter, their
  "this is the noisy one")

### What zone IS NOT

- **Not a failure-independence guarantee.** Two nodes with different
  zones in the same region may be in the same rack at one operator's
  homelab. We cannot verify physical independence.
- **Not a tenant-facing scheduling primitive.** Tenants requesting "HA
  across zones" should request HA across **regions** instead — that's
  the only granularity where independence is realistic.
- **Not a cross-operator concept.** `eu-central-1` from operator A
  has no relationship to `eu-central-1` from operator B; zones are
  scoped to whoever declared them.

These are honesty constraints. Pretending otherwise damages trust the
first time an operator's "two zones" turn out to be the same physical
machine.

---

## Reference data files

Three static JSON resources, served from the orchestrator. install.sh
fetches them at install time.

### `countries.json`

ISO 3166-1 alpha-2 codes with display names and supranational tags.
~250 entries.

```
src/Orchestrator/Resources/countries.json
```

Shape:
```json
[
  { "code": "TR", "name": "Türkiye", "tags": ["NATO", "CouncilOfEurope", "EU-CustomsUnion"] },
  ...
]
```

**Update cadence:**
- Codes: rare (ISO publishes maybe once per year, usually no material change)
- Tags: rare-but-political (Brexit, EU accession, treaty changes)

### `regions.json`

DeCloud-defined region taxonomy. ~20 entries.

```
src/Orchestrator/Resources/regions.json
```

Shape:
```json
[
  { "code": "eu-central", "name": "Europe Central", "continent": "EU" },
  ...
]
```

**Update cadence:** rare. Splitting a region requires careful
coordination because every consumer (scheduling, marketplace, cloud-init
resolvers) sees the change.

### `region-adjacency.json`

The adjacency graph used in soft locality scoring.

```
src/Orchestrator/Resources/region-adjacency.json
```

Shape:
```json
{
  "eu-central": ["eu-west", "eu-north", "eu-south", "eu-east"],
  ...
}
```

**Update cadence:** updated alongside `regions.json` whenever the
region list changes. Otherwise stable.

### `country-region-defaults.json`

Maps each country code to a default region — used by install.sh's
prompt to offer a sensible suggestion.

```
src/Orchestrator/Resources/country-region-defaults.json
```

Shape:
```json
{
  "TR": "eu-east",
  "DE": "eu-central",
  "US": "na-east",
  ...
}
```

**Update cadence:** updated alongside region splits. Otherwise stable.

---

## Data model

### Node schema

`NodeLocality` (replaces the previous `Region`/`Zone` strings):

```csharp
public class NodeLocality
{
    /// <summary>
    /// ISO 3166-1 alpha-2 country code. Operator-declared.
    /// "ZZ" indicates unknown / declined / pre-locality-standard.
    /// </summary>
    public string Country { get; set; } = "ZZ";

    /// <summary>
    /// Supranational membership tags derived from Country at registration.
    /// Examples: ["EU", "EEA", "Schengen", "NATO"].
    /// Recomputed on each registration; not directly mutable.
    /// </summary>
    public List<string> JurisdictionTags { get; set; } = new();

    /// <summary>
    /// DeCloud-defined region code. Must be a value from regions.json.
    /// "unknown" indicates pre-locality-standard.
    /// </summary>
    public string Region { get; set; } = "unknown";

    /// <summary>
    /// Operator-scoped zone, format <region>-<n>. Optional.
    /// </summary>
    public string? Zone { get; set; }

    /// <summary>
    /// Best-effort country code derived from request IP at registration.
    /// Used for corroboration only; not authoritative.
    /// </summary>
    public string? IpDerivedCountry { get; set; }

    /// <summary>
    /// True if Country and IpDerivedCountry disagree.
    /// Surfaced in marketplace listings and admin views.
    /// </summary>
    public bool LocationMismatch { get; set; }
}
```

### VmSpec schema

VM scheduling preferences become independent fields:

```csharp
public class VmSpec
{
    // ... existing fields ...

    /// <summary>
    /// Filter: jurisdiction tag the node must carry.
    /// Examples: "EU", "NATO", "USMCA".
    /// </summary>
    public string? RequiredJurisdictionTag { get; set; }

    /// <summary>
    /// Filter: country the node must reside in.
    /// </summary>
    public string? RequiredCountry { get; set; }

    /// <summary>
    /// Filter: countries the node must NOT reside in.
    /// </summary>
    public List<string>? ForbiddenCountries { get; set; }

    /// <summary>
    /// Preference: region (used as hard filter and locality scoring).
    /// </summary>
    public string? Region { get; set; }

    /// <summary>
    /// Preference: zone (hard filter only when set).
    /// </summary>
    public string? Zone { get; set; }
}
```

### Sample serialization

A registered node:
```json
{
  "id": "node-abc123",
  "locality": {
    "country":          "TR",
    "jurisdictionTags": ["NATO", "CouncilOfEurope", "EU-CustomsUnion"],
    "region":           "eu-east",
    "zone":             "eu-east-1",
    "ipDerivedCountry": "TR",
    "locationMismatch": false
  },
  ...
}
```

A VM with EU-jurisdiction requirement:
```json
{
  "spec": {
    "requiredJurisdictionTag": "EU",
    "region":                  "eu-central",
    "vCpus": 2, "memoryMB": 4096
  }
}
```

---

## API endpoints

Three read-only endpoints, all `[AllowAnonymous]` for marketplace and
install-time access.

### `GET /api/locality/countries`

Returns the contents of `countries.json`.

```bash
curl https://decloud.stackfi.tech/api/locality/countries
# → [
#     { "code": "TR", "name": "Türkiye", "tags": ["NATO", ...] },
#     ...
#   ]
```

### `GET /api/locality/regions`

Returns the contents of `regions.json`.

```bash
curl https://decloud.stackfi.tech/api/locality/regions
# → [
#     { "code": "eu-central", "name": "Europe Central", "continent": "EU" },
#     ...
#   ]
```

### `GET /api/locality/suggest/{country}`

Returns the suggested default region for a given country.

```bash
curl https://decloud.stackfi.tech/api/locality/suggest/TR
# → { "country": "TR", "region": "eu-east" }
```

### Existing endpoints (unchanged behavior, semantics clarified)

- `GET /api/nodes/regions` — aggregate listing of regions with active
  nodes. Still works; now operates on the constrained `regions.json`
  list.
- `GET /api/nodes/regions/{region}/zones` — same.

---

## Validation rules

### Country

- Pattern: `^[A-Z]{2}$`
- Must be present in `countries.json`
- Server-side validation in `NodeService.RegisterNodeAsync`
- Client-side mirror in install.sh (regex only; the existence check
  happens server-side after the prompt)

### Region

- Pattern: `^[a-z]+(-[a-z]+)*$`
- Must be present in `regions.json`
- Server-side validation
- Client-side mirror

### Zone

- Pattern: `^[a-z]+(-[a-z]+)*-[1-9][0-9]*$`
- Must start with `<region>-` where `<region>` is the node's declared region
- Server-side validation
- Client-side mirror

### `default` and `unknown` grandfathering

Existing nodes registered before the locality standard have:
```
country:           ZZ            (or absent)
region:            "default"     (legacy)
zone:              "default"     (legacy)
```

These pass validation as a special case until the node next
re-registers, at which point they must conform. install.sh on a
re-registration always sends conforming values (because it prompts or
auto-suggests). Manual `default/default` registration is rejected.

---

## Scheduling semantics

### Hard filters — locality is expressed as constraints

Locality requirements are **not** flat `VmSpec` fields. They are
tenant-authored constraints in `spec.Constraints`, evaluated in
FILTER 10 of `ApplyHardFiltersAsync` through `IConstraintEvaluator`
(see `SCHEDULING.md` §3/§7). Each is independent and means exactly
what it says:

| Requirement | Constraint |
| --- | --- |
| EU jurisdiction | `node.locality.jurisdictionTags contains "EU"` |
| Specific country | `node.locality.country eq "DE"` |
| Country exclusion list | `node.locality.country not_in ["RU", "BY"]` |
| Specific region | `node.locality.region eq "eu-central"` |
| Specific zone | `node.locality.zone eq "eu-central-1"` |
| Jurisdictional certainty | `node.locality.locationMismatch eq false` |

A node failing any constraint is excluded from scheduling
consideration — a categorical reject, not a score penalty.

### Soft scoring (`CalculateLocalityScore`)

**Currently neutral (0.5) for all nodes** — soft locality preferences
are a deferred design concern (see `SCHEDULING.md` §5/§11). Hard
locality requirements are fully expressible via the constraints above.
When soft preferences ship, the intended graduated model is:

| Match | Score |
| --- | --- |
| Same zone | 1.0 |
| Same region (different zone, or zone unspecified) | 0.8 |
| Adjacent region per `region-adjacency.json` | 0.5 |
| Same continent | 0.3 |
| No match | 0.0 |
| No preference specified | 0.5 (neutral) |

**Jurisdiction does not enter scoring.** It is a hard filter or it is
irrelevant — there is no "30% EU-compliant" node.

### Why this model matters

Hard filters are typed and granular (jurisdiction tag vs country vs
region vs zone), all through one constraint vocabulary and one
evaluator — the same evaluator that checks compliance when a node's
locality changes at re-registration. Soft scoring, once implemented,
will use the adjacency graph for genuinely useful "close but not
exact" results (a `eu-central` workload with no `eu-central` capacity
gets `eu-west` next, not random).

---

## install.sh prompt behavior

### Country detection and confirmation

```
[STEP] Detecting your location...
[INFO] IP-derived country: TR (best-effort, you can override)

Country (ISO 3166-1 alpha-2) [TR]:
```

If `/dev/tty` available: prompts with default. Operator presses enter
to accept.

If non-interactive: requires `--country` flag, errors otherwise.

### Region prompt

```
[INFO] Suggested region for TR: eu-east

Region [eu-east]:
   Available: na-east, na-central, na-west, sa-east, sa-west,
              eu-west, eu-central, eu-north, eu-south, eu-east,
              me-west, me-east, af-north, af-south, af-east,
              apac-northeast, apac-east, apac-southeast, apac-south,
              oceania
```

Default suggested via `GET /api/locality/suggest/{country}`. Operator
can override.

### Zone prompt

```
Zone (format: <region>-<n>) [eu-east-1]:
```

Defaults to `<region>-1`. Operator with multiple nodes increments the
number for each.

### Server-side mismatch detection

The orchestrator records `IpDerivedCountry` from the request's source
IP (best-effort GeoIP lookup) at registration time. If declared
`Country` and `IpDerivedCountry` differ:

- `LocationMismatch = true` is set on the node
- A warning is logged
- The marketplace UI surfaces a "claimed location differs from network
  location" indicator on the node card
- Tenants can decide how much weight to give this signal

This is **not** a rejection — VPNs, CGNAT, leased servers in foreign
datacenters all produce legitimate mismatches. But tenants paying a
premium for jurisdiction need to see the signal.

---

## Worked examples

### Example 1: Türkiye / Isparta MSI node

Operator runs install.sh on a server in Isparta, Türkiye:

```bash
curl -fsSL https://github.com/.../releases/latest/download/install.sh \
  | sudo bash -s -- \
      --orchestrator https://decloud.stackfi.tech \
      --wallet 0x86b8...
```

Prompts:
```
[INFO] IP-derived country: TR
Country [TR]: ↵
[INFO] Suggested region: eu-east
Region [eu-east]: ↵
Zone [eu-east-1]: ↵
```

Resulting node locality:
```json
{
  "country":          "TR",
  "jurisdictionTags": ["NATO", "CouncilOfEurope", "EU-CustomsUnion"],
  "region":           "eu-east",
  "zone":             "eu-east-1",
  "ipDerivedCountry": "TR",
  "locationMismatch": false
}
```

This node is:
- Discoverable by tenants filtering for `NATO` jurisdiction ✓
- Discoverable by tenants requiring `eu-east` region ✓
- Scored at 0.5 (adjacent) for tenants requesting `eu-central`
- Scored at 0.0 for tenants requesting `na-west`
- **Not** discoverable by tenants filtering for `EU` membership
  (Türkiye is in EU Customs Union but not EU itself — exactly the
  distinction tenants pay attention to)

### Example 2: GDPR-compliant US tenant

A US-based SaaS company has EU customers and needs GDPR-compliant
nodes for European data:

```json
{
  "spec": {
    "requiredJurisdictionTag": "EU",
    "vCpus": 4, "memoryMB": 8192
  }
}
```

Scheduler filters: only nodes whose `JurisdictionTags` contains `"EU"`
proceed. The Türkiye node above is **excluded** (no `EU` tag) — even
though it's geographically nearby — because the tenant's question is
about jurisdiction, not geography.

If the tenant also adds `"region": "eu-central"`, scheduling further
prefers Frankfurt-area nodes.

### Example 3: Low-latency placement, jurisdiction-agnostic

A trading firm wants the lowest possible latency to a Frankfurt
endpoint and doesn't care about jurisdiction:

```json
{
  "spec": {
    "region": "eu-central",
    "vCpus": 8, "memoryMB": 16384
  }
}
```

No jurisdiction filter. Hard filter on `region == "eu-central"`.
Locality scoring elevates `eu-central` zones; if no capacity, falls back
to `eu-west`/`eu-north`/`eu-south`/`eu-east` per the adjacency graph
(score 0.5).

### Example 4: Anti-pattern — country-as-region

Earlier free-form usage:
```json
{ "region": "us-east", "zone": "us-east-1a" }
```

Under the new standard:
```json
{
  "country": "US",
  "region":  "na-east",
  "zone":    "na-east-1"
}
```

A tenant filtering for `RequiredCountry = "US"` now gets a clean
answer, not a string-prefix parse.

---

## Migration from legacy `default/default`

Pre-standard nodes have `region: "default", zone: "default"`. The
migration path:

1. **Validation grandfathers `default/default`** until the node next
   re-registers. Node continues to function in scheduling
   (matched by exact equality with `Region = "default"` requests).
2. **install.sh ≥ 2.3.0** prompts for country and region using the new
   standard on every install run, including updates. After
   `decloud update`, the node re-registers with conforming values.
3. **A one-time migration job** (offline orchestrator script) can
   optionally backfill `country: ZZ` to the IP-derived country for
   existing `default/default` nodes that haven't updated. Region stays
   `default` until next operator-driven update.
4. **Marketplace listings** of `default/default` nodes show
   "Location: not declared" until upgrade. Tenants self-filter by
   excluding undeclared locations if they care.

No forced migration, no service downtime. Operators upgrade naturally
on the next update cycle.

---

## What is intentionally NOT here

- **City-level zoning.** Tempting (`eu-central-fra-1`), but operators
  rarely know their precise city, datacenter, or IXP. The IATA
  airport-code approach is well-defined but creates a much larger
  taxonomy with no clear maintenance owner.
- **Geographic coordinates.** WGS 84 lat/lon is the right answer for
  precise locality, but adds significant infrastructure (great-circle
  distance computation, latency-table maintenance) for marginal gain
  over region/zone today. Defer until there's a real driver.
- **Auto-derived jurisdiction.** Country is operator-declared with IP
  corroboration, never IP-determined. The IP is a corroboration
  signal, not authority — VPNs, CGNAT, and leased datacenter boxes all
  legitimately produce mismatches. The orchestrator records both and
  surfaces disagreement; operators are accountable for what they
  declare.
- **Cross-jurisdictional routing rules.** A VM in `DE` may route
  packets through `US` peering points; the data-flow path is not the
  same as the data-residence claim. Tenants with cross-border data
  transfer concerns need contractual agreements, not just a region
  filter.
- **Operator-attested independence zones.** A future tier where
  operators contractually attest to physical independence (separate
  power, separate network) could exist as a separate badge or tag.
  Today's zone semantics make no such promise; pretending otherwise
  damages trust on first failure.

---

## References

### Open standards used directly

- **ISO 3166-1 alpha-2** — country codes. Maintained by ISO 3166
  Maintenance Agency. https://www.iso.org/iso-3166-country-codes.html
- **ISO 3166-2** — country subdivisions (provinces, states). Reserved
  for future use if sub-national locality is ever added.
- **WGS 84** — latitude/longitude reference. Reserved for future use
  if precise coordinates are ever added.

### Open standards considered and rejected

- **UN M.49** — statistical region codes. Too coarse for scheduling
  (Asia is one bucket); designed for statistical aggregation, not
  network topology.
- **CLDR territory groupings** — Unicode's regional taxonomy. Same
  scope mismatch as M.49.
- **RIR regions** — Regional Internet Registry boundaries (ARIN,
  RIPE, etc.). Network-administrative reality, but only 5 globally —
  too coarse for our needs.
- **IATA airport codes** — well-defined city-anchored identifiers. May
  be revisited for future city-level zoning; out of scope today.

### DeCloud-owned data sources

- `src/Orchestrator/Resources/countries.json`
- `src/Orchestrator/Resources/regions.json`
- `src/Orchestrator/Resources/region-adjacency.json`
- `src/Orchestrator/Resources/country-region-defaults.json`

### Implementation files

- `src/Orchestrator/Services/NodeService.cs` — registration validation
  and tag derivation
- `src/Orchestrator/Services/VmScheduling/VmSchedulingService.cs` —
  hard filters and locality scoring
- `src/Orchestrator/Controllers/LocalityController.cs` — read-only
  `/api/locality/*` endpoints
- `install.sh` — interactive prompts, IP-derived country detection,
  client-side validation
- `src/DeCloud.NodeAgent.Infrastructure/Services/Metadata/NodeMetadataService.cs`
  — node-side locality state
