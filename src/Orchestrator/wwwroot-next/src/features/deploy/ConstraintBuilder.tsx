import type { Constraint, ConstraintVocabulary } from "./deploySubmit";
import {
  operatorsForTarget, valueIsList, scalarKindForType, constraintTargetGroup,
} from "./useDeploy";

// Scheduling-constraint builder (Phase 3 Customize parity, replacing the legacy
// constraint-builder.js). Entirely data-driven from the vocabulary endpoint —
// targets, their value types, and the operators valid per type all come from the
// server, so nothing here mirrors the constraint rules. Two tiers: template-
// imposed constraints are shown read-only (they travel in RecommendedSpec and a
// user can't relax them); the user adds their own below.

interface Props {
  vocab?: ConstraintVocabulary;
  locked: Constraint[];                 // template-imposed, read-only
  value: Constraint[];                  // user-added, editable
  onChange: (next: Constraint[]) => void;
}

function groupTargets(vocab?: ConstraintVocabulary): Record<string, string[]> {
  const groups: Record<string, string[]> = {};
  for (const t of vocab?.targets ?? []) {
    const g = constraintTargetGroup(t);
    (groups[g] ??= []).push(t);
  }
  return groups;
}

/** Render a stored value as text (locked display + text inputs). */
export function valueToText(value: unknown): string {
  if (Array.isArray(value)) return value.join(", ");
  if (value === true) return "true";
  if (value === false) return "false";
  return value == null ? "" : String(value);
}

/** Parse a text input back into the typed value the API expects. */
export function textToValue(
  text: string,
  isList: boolean,
  kind: "number" | "boolean" | "text",
): unknown {
  if (isList) return text.split(",").map((s) => s.trim()).filter(Boolean);
  if (kind === "number") { const n = Number(text); return text === "" || !Number.isFinite(n) ? text : n; }
  if (kind === "boolean") return text === "true";
  return text;
}

/** Sensible default value when the operator (and thus arity/type) is chosen. */
export function defaultValueFor(operator: string, targetType?: string): unknown {
  if (valueIsList(operator)) return [];
  if (scalarKindForType(targetType) === "boolean") return true;
  return "";
}

export function ConstraintBuilder({ vocab, locked, value, onChange }: Props) {
  const groups = groupTargets(vocab);

  const updateRow = (i: number, patch: Partial<Constraint>) =>
    onChange(value.map((c, idx) => (idx === i ? { ...c, ...patch } : c)));
  const removeRow = (i: number) => onChange(value.filter((_, idx) => idx !== i));
  const addRow = () => onChange([...value, { target: "", operator: "", value: "" }]);

  return (
    <div className="field">
      <span>Scheduling constraints</span>

      {locked.length > 0 && (
        <div style={{ display: "grid", gap: "var(--space-1)", marginBottom: "var(--space-1)" }}>
          {locked.map((c, i) => (
            <div key={`locked-${i}`} style={{ display: "flex", gap: 8, alignItems: "baseline", opacity: 0.8 }}>
              <span style={{ fontFamily: "var(--font-mono)", fontSize: "var(--text-sm)" }}>
                {c.target} {c.operator} {valueToText(c.value)}
              </span>
              <span style={{ color: "var(--text-secondary)", fontSize: "var(--text-xs)" }}>· set by template</span>
            </div>
          ))}
        </div>
      )}

      {value.map((c, i) => {
        const targetType = c.target ? vocab?.targetTypes[c.target] : undefined;
        const ops = operatorsForTarget(vocab, c.target);
        const isList = valueIsList(c.operator);
        const kind = scalarKindForType(targetType);
        return (
          <div key={i} style={{ display: "flex", gap: 8, alignItems: "center", flexWrap: "wrap" }}>
            <select
              value={c.target}
              onChange={(e) => updateRow(i, { target: e.target.value, operator: "", value: "" })}
            >
              <option value="">Target…</option>
              {Object.entries(groups).map(([g, ts]) => (
                <optgroup key={g} label={g}>
                  {ts.map((t) => <option key={t} value={t}>{t}</option>)}
                </optgroup>
              ))}
            </select>

            <select
              value={c.operator}
              disabled={!c.target}
              onChange={(e) => updateRow(i, {
                operator: e.target.value,
                value: defaultValueFor(e.target.value, targetType),
              })}
            >
              <option value="">op…</option>
              {ops.map((op) => <option key={op} value={op}>{op}</option>)}
            </select>

            {c.operator && (
              kind === "boolean" && !isList ? (
                <select
                  value={c.value === false ? "false" : "true"}
                  onChange={(e) => updateRow(i, { value: e.target.value === "true" })}
                >
                  <option value="true">true</option>
                  <option value="false">false</option>
                </select>
              ) : (
                <input
                  type={!isList && kind === "number" ? "number" : "text"}
                  placeholder={isList ? "comma, separated" : "value"}
                  value={valueToText(c.value)}
                  onChange={(e) => updateRow(i, { value: textToValue(e.target.value, isList, kind) })}
                />
              )
            )}

            <button
              type="button"
              className="btn-ghost"
              onClick={() => removeRow(i)}
              aria-label="Remove constraint"
              style={{ fontSize: "var(--text-sm)" }}
            >
              ✕
            </button>
          </div>
        );
      })}

      <button
        type="button"
        className="btn-ghost"
        onClick={addRow}
        style={{ justifySelf: "start", fontSize: "var(--text-sm)" }}
      >
        + Add constraint
      </button>

      <span style={{ color: "var(--text-secondary)", fontSize: "var(--text-xs)", lineHeight: 1.4 }}>
        Only nodes matching every constraint can host this VM. Fewer, broader constraints schedule faster.
      </span>
    </div>
  );
}
