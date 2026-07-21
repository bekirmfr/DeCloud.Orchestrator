# DeCloud — new app (`wwwroot-next`)

The React/TypeScript app that replaces the legacy vanilla app, page by page
(frontend remake, Option A). Served under **`/app`**; the legacy app keeps
serving `/` until cutover. See `FRONTEND_REMAKE_DESIGN.md` and
`FRONTEND_REMAKE_IMPLEMENTATION.md` for the full plan.

## Where this goes
Place this folder at `src/Orchestrator/wwwroot-next/` (sibling of `wwwroot/`).
It builds into `../wwwroot/dist-app`, which `Program.cs` serves at `/app`.

## One-time setup
1. Drop the folder in place, then: `cd src/Orchestrator/wwwroot-next && npm install`
2. Add the targets in `csproj-additions.xml` to `src/Orchestrator/Orchestrator.csproj`.
3. `Program.cs` already serves `/app` (serving Change 1 — the `/app` static
   provider + the amended `MapFallback`).

## Develop
- Backend: run the .NET app (`:5050`) as usual.
- Frontend: `npm run dev` → Vite on **`:3001`**, proxying `/api`, `/hub`, `/ws`
  to `:5050`. The legacy app's Vite (`:3000`) is untouched.
- Open `http://localhost:3001/app/`.

## Build (what Release does)
`npm run build` → `tsc -b && vite build` → `../wwwroot/dist-app`. In Release,
MSBuild runs this automatically via the `BuildFrontendNext` target.

## Structure
```
wwwroot-next/
├─ index.html            entry + pre-paint theme script (no flash)
├─ vite.config.ts        base '/app/', out ../wwwroot/dist-app, dev :3001 + proxy
├─ package.json          Phase 0 deps (react, vite, fontsource, openapi-typescript)
├─ csproj-additions.xml  paste into Orchestrator.csproj
├─ public/fonts/         (only if using the static fonts.css fallback)
└─ src/
   ├─ main.tsx           font + token + global imports, then mount
   ├─ App.tsx            Phase 0 proof page (delete when the shell lands, Phase 2)
   ├─ api/               generated API types go here (npm run gen:api)
   └─ styles/
      ├─ design-tokens.css  Meridian tokens (light + derived dark, AA-validated)
      ├─ fonts.css          static @font-face FALLBACK (Fontsource is primary)
      └─ global.css         reset + token-driven base
```

## API types
`npm run gen:api` generates `src/api/schema.d.ts` from the backend's
`/swagger/v1/swagger.json` (backend must be running). The boundary is uniform
(`ApiResponse<T>`, string enums), so the generated types are clean.

## Phase 0 done when
`/app` renders (Release via ASP.NET, Debug via Vite), `/` still shows the legacy
app, tokens + fonts load, light/dark toggles with no flash. Then Phase 1 (the
frozen auth core) and Phase 2 (the shell + first migrated page) begin.
