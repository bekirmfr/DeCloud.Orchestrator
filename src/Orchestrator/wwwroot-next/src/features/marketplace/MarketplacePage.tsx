import { Link, useSearchParams } from "react-router-dom";
import { type CSSProperties, type ReactNode } from "react";
import { useAuth } from "../../auth/AuthProvider";
import { useTemplates, useCategories, type VmTemplateSummary, type TemplateFilters } from "./useMarketplace";
import { MyTemplatesPage } from "../templates/MyTemplatesPage";

// Phase 5 · Marketplace. Two tabs (URL-driven, ?tab=): Search — the published
// template browse (GET /templates), URL-driven filters, into the deploy flow;
// and My Templates — the author's own templates (embedded MyTemplatesPage),
// moved here from its own nav item so authoring lives next to browsing.
//
// Price on each card is the SERVER's EstimatedCostPerHour — never computed here.

const gib = (bytes: number) => Math.round(bytes / 1024 ** 3);

const PILL: CSSProperties = {
  display: "inline-block", padding: "1px 8px", borderRadius: "var(--radius-pill)",
  fontSize: "var(--text-xs)", fontWeight: "var(--fw-medium)",
};

function TabBtn({ active, onClick, children }: { active: boolean; onClick: () => void; children: ReactNode }) {
  return (
    <button
      onClick={onClick}
      style={{
        padding: "8px 14px", background: "none", border: "none", cursor: "pointer",
        fontSize: "var(--text-sm)", fontWeight: active ? ("var(--fw-medium)" as CSSProperties["fontWeight"]) : "normal",
        color: active ? "var(--accent)" : "var(--text-tertiary)",
        borderBottom: active ? "2px solid var(--accent)" : "2px solid transparent",
      }}
    >
      {children}
    </button>
  );
}

function Badges({ t }: { t: VmTemplateSummary }) {
  return (
    <span style={{ display: "inline-flex", gap: 6, flexWrap: "wrap" }}>
      {t.isVerified && <span style={{ ...PILL, background: "var(--success-soft)", color: "var(--success)" }}>✓ Verified</span>}
      {t.isFeatured && <span style={{ ...PILL, background: "var(--accent-soft)", color: "var(--accent)" }}>★ Featured</span>}
      {t.isCommunity && <span style={{ ...PILL, background: "var(--surface-1)", color: "var(--text-secondary)" }}>Community</span>}
      {t.requiresGpu && <span style={{ ...PILL, background: "var(--warning-soft)", color: "var(--warning)" }}>GPU</span>}
    </span>
  );
}

function TemplateCard({ t }: { t: VmTemplateSummary }) {
  const [params] = useSearchParams();
  const node = params.get("node");
  return (
    <Link
      to={`/marketplace/${t.slug}${node ? `?node=${node}` : ""}`}
      className="card"
      style={{
        display: "flex", flexDirection: "column", gap: "var(--space-2)",
        padding: "var(--space-3)", textDecoration: "none", color: "inherit",
      }}
    >
      <div style={{ display: "flex", alignItems: "center", gap: "var(--space-2)" }}>
        {t.iconUrl
          ? <img src={t.iconUrl} alt="" width={28} height={28} style={{ borderRadius: "var(--radius-sm)" }} />
          : <span style={{ fontSize: 22 }}>📦</span>}
        <strong style={{ fontSize: "var(--text-md)" }}>{t.name}</strong>
      </div>

      <p style={{
        margin: 0, color: "var(--text-secondary)", fontSize: "var(--text-sm)", lineHeight: 1.4,
        display: "-webkit-box", WebkitLineClamp: 2, WebkitBoxOrient: "vertical", overflow: "hidden",
      }}>
        {t.description}
      </p>

      <Badges t={t} />

      <div className="mono" style={{ color: "var(--text-secondary)", fontSize: "var(--text-xs)" }}>
        {t.recommendedCpuCores} vCPU · {gib(t.recommendedMemoryBytes)} GB · {gib(t.recommendedDiskBytes)} GB
      </div>

      <div style={{ display: "flex", justifyContent: "space-between", alignItems: "baseline", marginTop: "auto" }}>
        <span className="mono" style={{ fontSize: "var(--text-sm)" }}>
          ≈ {t.estimatedCostPerHour.toFixed(4)} USDC/hr
        </span>
        {t.totalReviews > 0 && (
          <span style={{ color: "var(--text-secondary)", fontSize: "var(--text-xs)" }}>
            ★ {t.averageRating.toFixed(1)} ({t.totalReviews})
          </span>
        )}
      </div>
    </Link>
  );
}

export function MarketplacePage() {
  const { api } = useAuth();
  const [params, setParams] = useSearchParams();
  const tab = params.get("tab") === "mine" ? "mine" : "browse";

  const filters: TemplateFilters = {
    search: params.get("search") ?? "",
    category: params.get("category") ?? "",
    requiresGpu: params.get("gpu") === "1",
    sortBy: params.get("sort") ?? "popular",
  };

  // URL is the single source of truth for filters + tab (shareable, back-correct).
  const setParam = (key: string, value: string) => {
    const next = new URLSearchParams(params);
    if (value) next.set(key, value); else next.delete(key);
    setParams(next, { replace: true });
  };

  const { data: templates, isLoading, isError } = useTemplates(api, filters);
  const { data: categories } = useCategories(api);
  const featured = (templates ?? []).filter((t) => t.isFeatured);

  const inputStyle: CSSProperties = {
    padding: "6px 10px", borderRadius: "var(--radius-sm)",
    border: "1px solid var(--border)", fontSize: "var(--text-sm)",
  };

  return (
    <div style={{ display: "flex", flexDirection: "column", gap: "var(--space-4)" }}>
      <div>
        <span className="eyebrow muted">Marketplace</span>
        <h1 style={{ margin: "var(--space-2) 0 0" }}>Marketplace</h1>
      </div>

      <div style={{ display: "flex", gap: 4, borderBottom: "1px solid var(--border-subtle)" }}>
        <TabBtn active={tab === "browse"} onClick={() => setParam("tab", "")}>Search</TabBtn>
        <TabBtn active={tab === "mine"} onClick={() => setParam("tab", "mine")}>My Templates</TabBtn>
      </div>

      {tab === "mine" ? (
        <MyTemplatesPage embedded />
      ) : (
        <>
          {/* Filter bar — each control writes to the URL */}
          <div style={{ display: "flex", gap: "var(--space-2)", flexWrap: "wrap", alignItems: "center" }}>
            <input
              style={{ ...inputStyle, flex: "1 1 240px" }}
              placeholder="Search templates…"
              value={filters.search}
              onChange={(e) => setParam("search", e.target.value)}
            />
            <select style={inputStyle} value={filters.category} onChange={(e) => setParam("category", e.target.value)}>
              <option value="">All categories</option>
              {(categories ?? []).map((c) => (
                <option key={c.id} value={c.slug}>{c.iconEmoji} {c.name} ({c.templateCount})</option>
              ))}
            </select>
            <select style={inputStyle} value={filters.sortBy} onChange={(e) => setParam("sort", e.target.value)}>
              <option value="popular">Popular</option>
              <option value="newest">Newest</option>
              <option value="rating">Top rated</option>
            </select>
            <label style={{ display: "inline-flex", gap: 6, alignItems: "center", fontSize: "var(--text-sm)", color: "var(--text-secondary)" }}>
              <input type="checkbox" checked={filters.requiresGpu} onChange={(e) => setParam("gpu", e.target.checked ? "1" : "")} />
              GPU only
            </label>
          </div>

          {isLoading && <p style={{ color: "var(--text-secondary)" }}>Loading templates…</p>}
          {isError && <p style={{ color: "var(--danger)" }}>Couldn't load templates. Try again.</p>}
          {templates && templates.length === 0 && (
            <p style={{ color: "var(--text-secondary)" }}>No templates match these filters.</p>
          )}

          {/* Featured strip — only when not filtering, so it doesn't fight the results */}
          {featured.length > 0 && !filters.search && !filters.category && (
            <section style={{ display: "flex", flexDirection: "column", gap: "var(--space-2)" }}>
              <h2 style={{ margin: 0, fontSize: "var(--text-md)", color: "var(--text-secondary)" }}>Featured</h2>
              <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fill, minmax(260px, 1fr))", gap: "var(--space-3)" }}>
                {featured.map((t) => <TemplateCard key={`f-${t.id}`} t={t} />)}
              </div>
            </section>
          )}

          {templates && templates.length > 0 && (
            <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fill, minmax(260px, 1fr))", gap: "var(--space-3)" }}>
              {templates.map((t) => <TemplateCard key={t.id} t={t} />)}
            </div>
          )}
        </>
      )}
    </div>
  );
}
