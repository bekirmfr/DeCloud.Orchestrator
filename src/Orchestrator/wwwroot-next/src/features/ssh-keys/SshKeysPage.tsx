// The FIRST migrated page (Phase 2) — chosen because it's the lowest-stakes
// surface: a table + add/delete, NO wallet-signing, NO real-time. Migrating it
// proves the whole pipeline end-to-end (route → shell → api() → render → retire).
//
// Parity checklist (verify each before deleting the legacy SSH-keys module —
// DESIGN §7 mechanism):
//   [ ] list keys: name, fingerprint, created date
//   [ ] add key: name + public key; validation (name required, key format)
//   [ ] delete key: confirm
//   [ ] empty state (no keys yet)
//   [ ] loading state
//   [ ] error state (inline; the api() failure kind → message)
//
// TODO wiring: get `api` from the AuthProvider/useAuth (or a useApi() hook), then
// feed the useSshKeys hooks. Left abstract here so the data layer is swappable.

export function SshKeysPage() {
  // const api = useApi();
  // const { data, isLoading, error } = useSshKeys(api);
  return (
    <section>
      <header style={{ display: "flex", justifyContent: "space-between", alignItems: "baseline" }}>
        <h1 style={{ fontFamily: "var(--font-display)", fontWeight: 600 }}>SSH Keys</h1>
        {/* TODO: <button className="btn-primary" onClick={openAddModal}>Add key</button> */}
      </header>

      {/* TODO: states
          isLoading            → skeleton rows
          error                → inline error (error.kind → message)
          data.length === 0    → empty state
          else                 → <table> of keys with delete actions
      */}

      {/* TODO: <AddKeyModal open={...} onClose={...} /> */}
    </section>
  );
}
