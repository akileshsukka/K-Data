import { useState } from "react";
import { Link } from "react-router";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api } from "../../api/client";
import type { ImportChange, Issue } from "../../api/types";
import { usePlants } from "../../app/useCatalog";
import { Button } from "../../components/Button";
import { Dialog } from "../../components/Dialog";
import { Field, SelectInput, TextInput } from "../../components/Field";
import { PageHeader } from "../../components/PageHeader";
import { formatDateTime } from "../../components/dates";
import { useDebounced } from "../../components/useDebounced";
import { useToast } from "../../components/Toast";
import { OutcomeChip } from "../excel/ImportBits";
import { AdminTabs } from "./AdminTabs";

/**
 * Everything an applied Excel or client list could not do on its own: conflicts, invalid values, rejected rows and
 * warnings. Fix the data in the equipment profile (link), then mark the issue resolved with a short note.
 */
export function ImportIssuesPage() {
  const [resolved, setResolved] = useState(false);
  const [unitId, setUnitId] = useState("");
  const [q, setQ] = useState("");
  const search = useDebounced(q, 300);
  const [resolving, setResolving] = useState<ImportChange | null>(null);
  const plants = usePlants();
  const units = (plants.data ?? []).flatMap((p) => p.units);

  const issues = useQuery({
    queryKey: ["issues", resolved, unitId, search],
    queryFn: ({ signal }) => {
      const params = new URLSearchParams({ resolved: String(resolved) });
      if (unitId) params.set("unitId", unitId);
      if (search.trim()) params.set("q", search.trim());
      return api<Issue[]>(`/api/import-issues?${params}`, { signal });
    },
  });

  return (
    <div>
      <AdminTabs />
      <PageHeader title="Import issues" description="What uploaded files could not apply by themselves. Check each one in the equipment profile, then mark it resolved." />

      <div className="mt-5 grid gap-3 sm:grid-cols-[auto_12rem_1fr] sm:items-end">
        <div className="flex rounded-md border border-line bg-panel p-1" role="tablist">
          {[false, true].map((r) => (
            <button key={String(r)} type="button" role="tab" aria-selected={resolved === r} onClick={() => setResolved(r)}
              className={`min-h-8 rounded px-3 font-semibold ${resolved === r ? "bg-violet text-white" : "text-ink"}`}>
              {r ? "Resolved" : "Open"}
            </button>
          ))}
        </div>
        <SelectInput value={unitId} onChange={(e) => setUnitId(e.target.value)} aria-label="Unit" className="mt-0">
          <option value="">All units</option>
          {units.map((u) => <option key={u.id} value={u.id}>{u.name}</option>)}
        </SelectInput>
        <TextInput value={q} onChange={(e) => setQ(e.target.value)} placeholder="Search tag, Equipment ID or file" aria-label="Search" className="mt-0" />
      </div>

      {issues.isPending && <p className="mt-6 text-muted">Loading…</p>}
      {issues.data?.length === 0 && <p className="mt-6 text-muted">{resolved ? "No resolved issues yet." : "No open issues. Every uploaded change was applied."}</p>}
      {issues.data && issues.data.length > 0 && (
        <ul className="mt-5 space-y-2">
          {issues.data.map((i) => (
            <li key={i.change.id} className="grid gap-3 rounded-lg border border-line bg-panel p-4 md:grid-cols-[1fr_auto] md:items-start">
              <div className="min-w-0">
                <div className="flex flex-wrap items-center gap-2">
                  <OutcomeChip outcome={i.change.outcome} />
                  {i.change.assetId && i.change.assetCode ? (
                    <Link to={`/assets/${i.change.assetId}`} className="font-display text-lg font-semibold text-ink hover:text-violet">{i.change.assetTag ?? i.change.assetCode}</Link>
                  ) : (
                    <span className="font-display text-lg font-semibold text-ink">{i.change.assetTag ?? `Row ${i.change.rowNumber}`}</span>
                  )}
                  <span className="text-muted">· {i.change.fieldLabel}</span>
                </div>
                {i.change.reason && <p className="mt-1">{i.change.reason}</p>}
                {(i.change.excelValue || i.change.currentValue) && i.change.field !== "(row)" && (
                  <p className="mt-1 text-sm break-words text-muted">
                    File: <strong className="text-text">{i.change.excelValue ?? "empty"}</strong>
                    {i.change.currentValue !== null && <> · app: <strong className="text-text">{i.change.currentValue}</strong></>}
                  </p>
                )}
                <p className="mt-1 text-sm text-muted">
                  <Link to={`/imports/${i.change.runId}`} className="font-semibold text-violet">{i.fileName}</Link> · {i.unitName} · {formatDateTime(i.uploadedAtUtc)}{i.uploadedBy && ` by ${i.uploadedBy}`}
                </p>
                {i.change.resolution === "Resolved" && (
                  <p className="mt-1 text-sm text-done">Resolved {formatDateTime(i.change.resolvedAtUtc)} by {i.change.resolvedBy}{i.change.resolutionNote ? `: ${i.change.resolutionNote}` : ""}</p>
                )}
              </div>
              <Button variant={resolved ? "secondary" : "primary"} onClick={() => setResolving(i.change)}>{resolved ? "Reopen" : "Mark resolved"}</Button>
            </li>
          ))}
        </ul>
      )}

      <ResolveDialog change={resolving} onClose={() => setResolving(null)} />
    </div>
  );
}

function ResolveDialog({ change, onClose }: { change: ImportChange | null; onClose: () => void }) {
  const [note, setNote] = useState("");
  const queryClient = useQueryClient();
  const toast = useToast();
  const reopen = change?.resolution === "Resolved";
  const save = useMutation({
    mutationFn: () => api(`/api/import-issues/${change!.id}`, { method: "POST", body: { resolved: !reopen, note: note || null } }),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["issues"] });
      toast(reopen ? "Issue reopened" : "Issue resolved");
      setNote("");
      onClose();
    },
    onError: (e) => toast(e.message, "error"),
  });

  return (
    <Dialog open={!!change} title={reopen ? "Reopen issue" : "Mark as resolved"} onClose={onClose}>
      <form onSubmit={(e) => { e.preventDefault(); save.mutate(); }} className="space-y-4">
        <p className="text-muted">{change?.assetTag ?? change?.assetCode} · {change?.fieldLabel}</p>
        <Field label="Note (optional)" hint="What was done, e.g. “Value confirmed with the engineer and set in the app”.">
          <TextInput value={note} onChange={(e) => setNote(e.target.value)} maxLength={1000} autoFocus />
        </Field>
        <div className="flex justify-end gap-2">
          <Button type="button" variant="secondary" onClick={onClose}>Cancel</Button>
          <Button type="submit" busy={save.isPending}>{reopen ? "Reopen" : "Mark resolved"}</Button>
        </div>
      </form>
    </Dialog>
  );
}
