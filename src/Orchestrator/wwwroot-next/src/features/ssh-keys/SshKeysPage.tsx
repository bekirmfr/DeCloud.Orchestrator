import { useState } from "react";
import { useAuth } from "../../auth/AuthProvider";
import { useSshKeys, useDeleteSshKey, type SshKey } from "./useSshKeys";
import { AddKeyModal } from "./AddKeyModal";
import type { AppError } from "../../api/errors";

// The FIRST migrated page (Phase 2) — lowest-stakes surface: a table + add/delete,
// no wallet-signing, no real-time. Proves the pipeline route→shell→api()→render→retire.
// Behavior matches legacy app.js (loadSSHKeys / addSSHKey / deleteSSHKey): list,
// add via modal, delete with confirm. States: loading / error / empty / list.

export function SshKeysPage() {
  const { api } = useAuth();
  const { data, isLoading, error } = useSshKeys(api);
  const del = useDeleteSshKey(api);
  const [addOpen, setAddOpen] = useState(false);

  function onDelete(k: SshKey) {
    // Parity with legacy confirm() gate.
    if (!window.confirm(`Delete SSH key "${k.name}"?`)) return;
    del.mutate(k.id);
  }

  return (
    <section>
      <header style={{ display: "flex", justifyContent: "space-between", alignItems: "baseline", marginBottom: 20 }}>
        <h1 style={{ fontFamily: "var(--font-display)", fontWeight: 600 }}>SSH Keys</h1>
        <button className="btn-primary" onClick={() => setAddOpen(true)}>Add key</button>
      </header>

      {isLoading && <p style={{ color: "var(--text-secondary)" }}>Loading…</p>}

      {error && (
        <p role="alert" style={{ color: "var(--danger)" }}>
          {(error as AppError)?.message ?? "Couldn't load SSH keys."}
        </p>
      )}

      {!isLoading && !error && data && data.length === 0 && (
        <p style={{ color: "var(--text-secondary)" }}>
          No SSH keys yet. Add one to access your VMs over SSH.
        </p>
      )}

      {!isLoading && !error && data && data.length > 0 && (
        <table style={{ width: "100%", borderCollapse: "collapse" }}>
          <thead>
            <tr style={{ textAlign: "left", color: "var(--text-secondary)", fontSize: 13 }}>
              <th style={{ padding: "8px 12px" }}>Name</th>
              <th style={{ padding: "8px 12px", fontFamily: "var(--font-mono)" }}>Fingerprint</th>
              <th style={{ padding: "8px 12px" }}>Added</th>
              <th style={{ padding: "8px 12px" }} aria-label="actions" />
            </tr>
          </thead>
          <tbody>
            {data.map((k) => (
              <tr key={k.id} style={{ borderTop: "1px solid var(--border-subtle)" }}>
                <td style={{ padding: "10px 12px" }}>{k.name}</td>
                <td style={{ padding: "10px 12px", fontFamily: "var(--font-mono)", fontSize: 12.5 }}>
                  {k.fingerprint}
                </td>
                <td style={{ padding: "10px 12px", color: "var(--text-secondary)" }}>
                  {new Date(k.createdAt).toLocaleDateString()}
                </td>
                <td style={{ padding: "10px 12px", textAlign: "right" }}>
                  <button
                    className="btn-ghost"
                    onClick={() => onDelete(k)}
                    disabled={del.isPending && del.variables === k.id}
                  >
                    Delete
                  </button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}

      <AddKeyModal api={api} open={addOpen} onClose={() => setAddOpen(false)} />
    </section>
  );
}
