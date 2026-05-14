import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

// Vite-konfigurationen er bevidst minimal: ét entry-punkt, ingen aliaser,
// ingen proxy. SSE-target'et bestemmes af miljøvariablen
// VITE_ALERTING_BASE_URL der indlejres ved build.
export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    host: true,
  },
});
