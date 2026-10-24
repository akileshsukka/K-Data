import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { RouterProvider } from "react-router";
import { ApiError } from "./api/client";
import { SessionGate } from "./app/session";
import { router } from "./app/router";
import { ToastProvider } from "./components/Toast";
import "./styles/index.css";

const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      staleTime: 30_000,
      refetchOnWindowFocus: true,
      // Do not retry answers that will not change (no access, not found, validation).
      retry: (count, error) => !(error instanceof ApiError && error.status < 500) && count < 2,
    },
  },
});

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    <QueryClientProvider client={queryClient}>
      <ToastProvider>
        <SessionGate>
          <RouterProvider router={router} />
        </SessionGate>
      </ToastProvider>
    </QueryClientProvider>
  </StrictMode>,
);
