// Import order matters: fonts register @font-face, tokens define the CSS
// variables, global applies the reset/base that consumes them.

// Fonts — self-hosted via Fontsource (base-aware, bundled + fingerprinted by
// Vite; no manual /fonts paths). These are the exact families/weights Meridian
// uses. (Static @font-face alternative lives in styles/fonts.css — see its note
// about the /app base if you go that route instead.)
import "@fontsource-variable/space-grotesk"; // display
import "@fontsource-variable/inter"; // body
import "@fontsource/jetbrains-mono/400.css"; // data/figures
import "@fontsource/jetbrains-mono/500.css";

import "./styles/design-tokens.css";
import "./styles/global.css";

import React from "react";
import { createRoot } from "react-dom/client";
import App from "./App";

const el = document.getElementById("root");
if (!el) throw new Error("#root not found");

createRoot(el).render(
  <React.StrictMode>
    <App />
  </React.StrictMode>
);
