import { createContext, useContext, type ReactNode } from "react";
import { useQuery } from "@tanstack/react-query";
import { api, ApiError } from "../api/client";
import type { Me } from "../api/types";
import { NoAccessPage } from "../features/auth/NoAccessPage";
import { EmergencyLoginPage } from "../features/auth/EmergencyLoginPage";
import { FullPageMessage } from "../components/FullPageMessage";

const SessionContext = createContext<Me | null>(null);

export function useSession(): Me & { can: (permission: string) => boolean } {
  const me = useContext(SessionContext);
  if (!me) throw new Error("useSession outside SessionGate");
  return { ...me, can: (p: string) => me.permissions.includes(p) };
}

/** Loads the signed-in user once; shows the right screen when there is no access. */
export function SessionGate({ children }: { children: ReactNode }) {
  const query = useQuery({
    queryKey: ["me"],
    queryFn: ({ signal }) => api<Me>("/api/me", { signal }),
    retry: false,
    staleTime: 5 * 60_000,
  });

  if (query.isPending) return <FullPageMessage title="Loading K Data" />;

  if (query.error) {
    const error = query.error as ApiError;
    if (error.code === "not_registered") return <NoAccessPage message={error.message} />;
    if (error.code === "emergency_login") return <EmergencyLoginPage />;
    return (
      <FullPageMessage
        title="Your session could not be opened"
        detail={error.message}
        action={{ label: "Try again", onClick: () => window.location.reload() }}
      />
    );
  }

  return <SessionContext.Provider value={query.data}>{children}</SessionContext.Provider>;
}
