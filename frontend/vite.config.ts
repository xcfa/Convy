import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

// Convy serves the built UI from src/Convy/wwwroot. In development `npm run dev` proxies
// the API and the sign-in endpoints to Convy running on its launchSettings port.
const convy = process.env.CONVY_URL ?? "http://localhost:5174";

export default defineConfig({
  plugins: [react()],
  build: {
    outDir: "../src/Convy/wwwroot",
    emptyOutDir: true,
  },
  server: {
    proxy: {
      "/api": convy,
      "/auth": convy,
      "/signin-oidc": convy,
    },
  },
});
