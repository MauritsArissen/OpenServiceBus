import path from "path";
import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import tailwindcss from "@tailwindcss/vite";

// Builds straight into the Explorer's wwwroot - `dotnet run` then serves the SPA.
export default defineConfig({
  plugins: [react(), tailwindcss()],
  resolve: { alias: { "@": path.resolve(__dirname, "./src") } },
  build: { outDir: "../wwwroot", emptyOutDir: true },
  server: { proxy: { "/api": "http://localhost:5400" } },
});
