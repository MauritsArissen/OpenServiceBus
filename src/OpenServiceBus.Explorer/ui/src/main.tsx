import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import App from "./App.tsx";
import "./index.css";
import { LS_THEME } from "./store";

// Apply persisted theme before first paint (avoids a light-to-dark flash).
if ((localStorage.getItem(LS_THEME) ?? "light") === "dark") {
  document.documentElement.classList.add("dark");
}

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    <App />
  </StrictMode>,
);
