import { Link, useParams } from "react-router-dom";
import { type CSSProperties } from "react";
import { useAuth } from "../../auth/AuthProvider";
import { useTemplate } from "../deploy/useDeploy";
import { useReviews, shortWallet, type MarketplaceReview } from "./useMarketplace";

// Phase 5 · Marketplace template detail. Full template info, the SERVER's price
// (estimatedCostPerHour — never a client table; that's what retires
// template-detail.js), reviews, and Deploy into the existing flow. Reached from
// the browse grid; the live per-spec price breakdown lives on the deploy page.

const gib = (bytes?: number | null) => (bytes ? Math.round(bytes / 1024 ** 3) : 0);

const chip: CSSProperties = {
  display: "inline-block", padding: "1px 8px", borderRadius: "var(--radius-pill)",
  fontSize: "var(--text-xs)", fontWeight: "var(--fw-medium)",
  background: "var(--surface-1)", color: "var(--text-secondary)",
};

function Stars({ rating }: { rating: number }) {
  const r = Math.max(0, Math.min(5, Math.round(rating)));
  return <span style={{ color: "var(--warning)" }}>{"★".repeat(r)}<span style={{ color: "var(--border)" }}>{"★".repeat(5 - r)}</span></span>;
}

function ReviewRow({ r }: { r: MarketplaceReview }) {
  return (
    <div style={{ display: "flex", flexDirection: "column", gap: 2, padding: "var(--space-2) 0", borderTop: "1px solid var(--border-subtle)" }}>
      <div style={{ display: "flex", justifyContent: "space-between", alignItems: "baseline", gap: 8 }}>
        <span style={{ fontSize: "var(--text-sm)", fontWeight: "var(--fw-medium)" }}>
          {r.reviewerName || shortWallet(r.reviewerId)}
        </span>
        <span style={{ display: "flex", gap: 8, alignItems: "baseline" }}>
          <Stars rating={r.rating} />
          <span style={{ color: "var(--text-secondary)", fontSize: "var(--text-xs)" }}>
            {new Date(r.createdAt).toLocaleDateString()}
          </span>
        </span>
      </div>
      {r.title && <strong style={{ fontSize: "var(--text-sm)" }}>{r.title}</strong>}
      {r.comment && <p style={{ margin: 0, color: "var(--text-secondary)", fontSize: "var(--text-sm)", lineHeight: 1.4 }}>{r.comment}</p>}
    </div>
  );
}

export function MarketplaceDetailPage() {
  const { api } = useAuth();
  const { slug } = useParams();

  // Hooks above guards (Rules of Hooks); reviews wait on the template id.
  const { data: template, isLoading, isError } = useTemplate(api, slug ?? "");
  const reviews = useReviews(api, template?.id);

  if (isLoading) return <p style={{ color: "var(--text-secondary)" }}>Loading template…</p>;
  if (isError || !template) {
    return (
      <div>
        <p style={{ color: "var(--danger)" }}>Couldn't load this template.</p>
        <Link to="/marketplace" className="nav-link">← Back to Marketplace</Link>
      </div>
    );
  }

  const rec = template.recommendedSpec;
  const revs = reviews.data ?? [];
  const avg = revs.length ? revs.reduce((s, r) => s + r.rating, 0) / revs.length : 0;

  return (
    <div style={{ display: "flex", flexDirection: "column", gap: "var(--space-4)", maxWidth: 820 }}>
      <Link to="/marketplace" className="nav-link" style={{ alignSelf: "start" }}>← Marketplace</Link>

      {/* Header + Deploy */}
      <div style={{ display: "flex", justifyContent: "space-between", alignItems: "flex-start", gap: "var(--space-3)", flexWrap: "wrap" }}>
        <div style={{ display: "flex", gap: "var(--space-2)", alignItems: "center" }}>
          {template.iconUrl
            ? <img src={template.iconUrl} alt="" width={40} height={40} style={{ borderRadius: "var(--radius-sm)" }} />
            : <span style={{ fontSize: 32 }}>📦</span>}
          <div>
            <h1 style={{ margin: 0 }}>{template.name}</h1>
            <div style={{ display: "flex", gap: 6, marginTop: 4 }}>
              {template.category && <span style={chip}>{template.category}</span>}
              {template.requiresGpu && <span style={{ ...chip, background: "var(--warning-soft)", color: "var(--warning)" }}>GPU</span>}
            </div>
          </div>
        </div>
        <Link className="btn-primary" to={`/marketplace/${template.slug}/deploy`}>Deploy</Link>
      </div>

      {/* Description */}
      {(template.longDescription || template.description) && (
        <p style={{ margin: 0, lineHeight: 1.5 }}>{template.longDescription || template.description}</p>
      )}

      {/* Recommended spec + price */}
      <div className="card" style={{ padding: "var(--space-3)", border: "1px solid var(--border)", borderRadius: "var(--radius-md)", display: "flex", flexDirection: "column", gap: "var(--space-2)" }}>
        <strong>Recommended configuration</strong>
        {rec ? (
          <div style={{ color: "var(--text-secondary)", fontFamily: "var(--font-mono)", fontSize: "var(--text-sm)" }}>
            {rec.virtualCpuCores} vCPU · {gib(rec.memoryBytes)} GB RAM · {gib(rec.diskBytes)} GB disk
          </div>
        ) : (
          <div style={{ color: "var(--text-secondary)", fontSize: "var(--text-sm)" }}>Configured at deploy time.</div>
        )}
        <div style={{ display: "flex", gap: "var(--space-3)", flexWrap: "wrap", alignItems: "baseline" }}>
          {typeof template.estimatedCostPerHour === "number" && (
            <span style={{ fontFamily: "var(--font-mono)" }}>≈ {template.estimatedCostPerHour.toFixed(4)} USDC/hr</span>
          )}
          {!!template.templatePrice && template.templatePrice > 0 && (
            <span style={{ color: "var(--text-secondary)", fontSize: "var(--text-sm)" }}>
              + {template.templatePrice} USDC one-time template fee
            </span>
          )}
        </div>
        <span style={{ color: "var(--text-secondary)", fontSize: "var(--text-xs)" }}>
          Adjust CPU, memory, OS, durability and more on the deploy screen — the live price updates there.
        </span>
      </div>

      {/* Reviews */}
      <section style={{ display: "flex", flexDirection: "column", gap: "var(--space-1)" }}>
        <div style={{ display: "flex", gap: 8, alignItems: "baseline" }}>
          <h2 style={{ margin: 0, fontSize: "var(--text-md)" }}>Reviews</h2>
          {revs.length > 0 && (
            <span style={{ color: "var(--text-secondary)", fontSize: "var(--text-sm)" }}>
              <Stars rating={avg} /> {avg.toFixed(1)} ({revs.length})
            </span>
          )}
        </div>
        {reviews.isLoading && <p style={{ color: "var(--text-secondary)", fontSize: "var(--text-sm)" }}>Loading reviews…</p>}
        {!reviews.isLoading && revs.length === 0 && (
          <p style={{ color: "var(--text-secondary)", fontSize: "var(--text-sm)" }}>No reviews yet.</p>
        )}
        {revs.map((r) => <ReviewRow key={r.id} r={r} />)}
      </section>
    </div>
  );
}
