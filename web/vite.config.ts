import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import tailwindcss from "@tailwindcss/vite";

// In development the API runs on 127.0.0.1:5080 (dotnet run) and Vite proxies /api to it.
// The production build goes straight into the API's wwwroot, served by the same service.
export default defineConfig({
  plugins: [react(), tailwindcss()],
  server: {
    port: 5173,
    proxy: { "/api": "http://127.0.0.1:5080" },
  },
  build: {
    outDir: "../src/PresApp.Api/wwwroot",
    emptyOutDir: true,
  },
});
