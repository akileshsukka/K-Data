import { formatDateTime } from "../../components/dates";
import { PageHeader } from "../../components/PageHeader";
import { AdminTabs } from "./AdminTabs";
import { useLogins } from "./useAdmin";

const methodLabel: Record<string, string> = {
  Cloudflare: "E-mail code",
  Emergency: "Emergency",
  Development: "Development",
};

/** One row per sign-in (not per click): who entered, when, how and whether it was allowed. */
export function LoginsPage() {
  const logins = useLogins();
  return (
    <div>
      <AdminTabs />
      <PageHeader title="Sign-in log" description="The last 300 sign-ins and rejected attempts, in Dubai time." />
      {logins.isPending && <p className="mt-6 text-muted">Loading…</p>}
      {logins.data?.length === 0 && <p className="mt-6 text-muted">No sign-ins recorded yet.</p>}
      {logins.data && logins.data.length > 0 && (
        <div className="mt-6 overflow-x-auto rounded-lg border border-line bg-panel">
          <table className="w-full min-w-[40rem] border-separate border-spacing-0 text-left">
            <thead className="font-display text-muted">
              <tr>
                <th className="border-b border-line px-4 py-3 font-semibold">Date</th>
                <th className="border-b border-line px-4 py-3 font-semibold">E-mail</th>
                <th className="border-b border-line px-4 py-3 font-semibold">Method</th>
                <th className="border-b border-line px-4 py-3 font-semibold">Result</th>
                <th className="border-b border-line px-4 py-3 font-semibold">IP</th>
              </tr>
            </thead>
            <tbody>
              {logins.data.map((l, i) => (
                <tr key={i}>
                  <td className="border-b border-line px-4 py-2.5 whitespace-nowrap">{formatDateTime(l.atUtc)}</td>
                  <td className="border-b border-line px-4 py-2.5">{l.email}</td>
                  <td className="border-b border-line px-4 py-2.5">{methodLabel[l.method] ?? l.method}</td>
                  <td className={`border-b border-line px-4 py-2.5 ${l.success ? "text-done" : "text-hold"}`}>
                    {l.success ? "Signed in" : (l.detail ?? "Rejected")}
                  </td>
                  <td className="border-b border-line px-4 py-2.5 text-sm text-muted">{l.ipAddress ?? "—"}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}
