import { useMemo, useState } from "react";
import { Link, useParams, useSearchParams } from "react-router";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api } from "../../api/client";
import type { SimproItem, SimproRunDetail } from "../../api/types";
import { Button } from "../../components/Button";
import { Dialog } from "../../components/Dialog";
import { PageHeader } from "../../components/PageHeader";
import { formatDateTime } from "../../components/dates";
import { useToast } from "../../components/Toast";
import { ItemOutcome, RunStatus } from "./SimproBits";

const tabs = [
  { key: "held", label: "Needs a decision", outcomes: ["Changed in Simpro", "Conflict", "Gate goes back", "Waiting"] },
  { key: "write", label: "Changes", outcomes: ["To write", "Written"] },
  { key: "new", label: "New equipment", outcomes: ["To create", "Created", "Not created"] },
  { key: "invalid", label: "Invalid", outcomes: ["Invalid"] },
  { key: "rules", label: "Rules", outcomes: ["Rule"] },
  { key: "reference", label: "Identifiers", outcomes: ["Reference"] },
] as const;

/** One preview or sync: what was (or would be) written, what is held and why, and the decisions to unblock it. */
export function SimproRunPage() {
  const id = Number(useParams().id);
  const [params, setParams] = useSearchParams();
  const toast = useToast();
  const queryClient = useQueryClient();
  const [undoOpen, setUndoOpen] = useState(false);
  const detail = useQuery({ queryKey: ["simpro", "run", id], queryFn: ({ signal }) => api<SimproRunDetail>(`/api/simpro/runs/${id}`, { signal }) });

  const items = detail.data?.items ?? [];
  const counts = Object.fromEntries(tabs.map((t) => [t.key, new Set(items.filter((i) => (t.outcomes as readonly string[]).includes(i.outcome)).map((i) => t.key === "write" || t.key === "held" ? i.assetId : i.id)).size]));
  const firstTab = tabs.find((t) => counts[t.key] > 0)?.key ?? "write";
  const tab = (params.get("tab") as (typeof tabs)[number]["key"]) ?? firstTab;
  const current = tabs.find((t) => t.key === tab) ?? tabs[1];
  const groups = useMemo(() => {
    const map = new Map<string, SimproItem[]>();
    for (const i of items.filter((x) => (current.outcomes as readonly string[]).includes(x.outcome))) map.set(i.assetId, [...(map.get(i.assetId) ?? []), i]);
    return [...map.values()];
  }, [items, current]);

  const undo = useMutation({
    mutationFn: () => api<SimproRunDetail>(`/api/simpro/runs/${id}/undo`, { method: "POST" }),
    onSuccess: (d) => {
      setUndoOpen(false);
      queryClient.invalidateQueries({ queryKey: ["simpro"] });
      toast(d.run.status === "Applied" ? d.run.message ?? "Run undone" : d.run.message ?? "The run could not be undone.", d.run.status === "Applied" ? "success" : "error");
    },
    onError: (e) => { setUndoOpen(false); toast(e.message, "error"); },
  });

  if (detail.isPending) return <p className="text-muted">Loading…</p>;
  if (detail.isError) return <p className="text-hold">{detail.error.message}</p>;
  const { run } = detail.data;
  const preview = run.mode === "Preview";

  return (
    <div>
      <PageHeader
        title={preview ? "Simpro preview" : run.mode === "Undo" ? "Undo" : "Simpro sync"}
        description={`${formatDateTime(run.startedAtUtc)} · ${run.triggeredBy === "schedule" ? "daily schedule" : run.triggeredBy}`}
        actions={
          <>
            <Link to="/admin/simpro" className="inline-flex min-h-10 items-center rounded-md border border-line bg-panel px-4 font-semibold text-ink hover:border-violet hover:text-violet">All runs</Link>
            {run.canUndo && <Button variant="danger" onClick={() => setUndoOpen(true)}>Undo this sync</Button>}
          </>
        }
      />
      <div className="mt-4 flex flex-wrap items-center gap-3">
        <RunStatus run={run} />
        {run.message && <span className="text-muted">{run.message}</span>}
      </div>

      {run.mode !== "Undo" && run.status !== "Failed" && (
        <section className="mt-5 grid grid-cols-2 gap-3 md:grid-cols-5" aria-label="Summary">
          <Count label={preview ? "To update" : "Updated"} value={run.updatedCount} detail={`${run.fieldCount} fields`} />
          <Count label={preview ? "To create" : "Created"} value={run.createdCount} detail="New equipment" />
          <Count label="Held" value={run.heldCount} detail="Waiting for a decision" tone={run.heldCount > 0 ? "ongoing" : undefined} />
          <Count label="Invalid" value={run.rejectedCount} detail="Not written or not created" tone={run.rejectedCount > 0 ? "hold" : undefined} />
          <Count label="Only in Simpro" value={run.notInAppCount} detail="Never touched" />
        </section>
      )}

      {items.length > 0 && (
        <>
          <div className="mt-6 flex flex-wrap gap-2" role="tablist">
            {tabs.map((t) => (
              <button key={t.key} type="button" role="tab" aria-selected={tab === t.key} onClick={() => setParams({ tab: t.key }, { replace: true })}
                className={`min-h-9 rounded-full border px-3 text-sm font-semibold ${tab === t.key ? "border-violet bg-violet text-white" : "border-line bg-panel text-ink hover:border-violet"}`}>
                {t.label} <span className="tabular-nums opacity-80">{counts[t.key]}</span>
              </button>
            ))}
          </div>
          <div className="mt-3 flex flex-wrap items-start gap-3">
            <p className="max-w-3xl flex-1 text-sm text-muted">{Hint[current.key]}</p>
            {current.key === "held" && counts.held > 0 && <ForceAll runId={id} count={counts.held} />}
          </div>
          {groups.length === 0 ? <p className="mt-4 text-muted">Nothing here.</p> : (
            <ul className="mt-4 space-y-3">
              {groups.map((rows) => <AssetItems key={rows[0].assetId} rows={rows} runId={id} held={current.key === "held"} />)}
            </ul>
          )}
        </>
      )}

      <Dialog open={undoOpen} title="Undo this sync?" onClose={() => setUndoOpen(false)}>
        <p>Every Simpro value this run changed goes back to what it was, new equipment it created are removed and its history events are deleted.</p>
        <p className="mt-2 text-muted">It is refused if someone edited those equipment in Simpro after the run. K Data is not changed.</p>
        <div className="mt-5 flex justify-end gap-2">
          <Button variant="secondary" onClick={() => setUndoOpen(false)}>Cancel</Button>
          <Button variant="danger" busy={undo.isPending} onClick={() => undo.mutate()}>Undo</Button>
        </div>
      </Dialog>
    </div>
  );
}

const Hint: Record<string, string> = {
  held: "Nothing is written for these equipment until you decide. If Simpro is right, copy its value into K Data; if K Data is right, let K Data win (it is written in the next sync).",
  write: "Values K Data writes to Simpro. The previous Simpro value is shown first.",
  new: "Equipment in K Data that do not exist in Simpro yet. They are created with their Equipment ID, workpack item and history event.",
  invalid: "Values that cannot be written (not in the list, too long, missing Simpro location). Fix them in K Data.",
  rules: "Values set by the rules (highest completed gate, Removed from Site, date of the latest reading). Simpro gets them; consider setting them in K Data too.",
  reference: "Equipment ID or tag differ between K Data and Simpro. They are never written: check which one is right.",
};

function Count({ label, value, detail, tone }: { label: string; value: number; detail: string; tone?: "hold" | "ongoing" }) {
  const border = tone === "hold" ? "border-hold/40" : tone === "ongoing" ? "border-ongoing/40" : "border-line";
  return (
    <div className={`rounded-lg border bg-panel p-4 ${border}`}>
      <p className="text-sm font-semibold text-muted">{label}</p>
      <p className="font-display text-[2rem] leading-none font-bold text-ink tabular-nums">{value}</p>
      <p className="mt-1 text-sm text-muted">{detail}</p>
    </div>
  );
}

function AssetItems({ rows, runId, held }: { rows: SimproItem[]; runId: number; held: boolean }) {
  const first = rows[0];
  const toast = useToast();
  const queryClient = useQueryClient();
  const force = useMutation({
    mutationFn: (on: boolean) => api(`/api/simpro/force/${first.assetId}?force=${on}`, { method: "POST" }),
    onSuccess: (_, on) => { queryClient.invalidateQueries({ queryKey: ["simpro"] }); toast(on ? "K Data wins in the next sync" : "Decision removed"); },
    onError: (e) => toast(e.message, "error"),
  });

  return (
    <li className="rounded-lg border border-line bg-panel">
      <div className="flex flex-wrap items-center gap-x-3 gap-y-2 border-b border-line px-4 py-2.5">
        <Link to={`/assets/${first.assetId}`} className="font-display text-lg font-semibold text-ink hover:text-violet">{first.assetTag ?? first.assetCode}</Link>
        <span className="mr-auto text-sm text-muted">{first.assetCode} · {first.unitName}</span>
        {held && (first.forced
          ? <Button variant="secondary" busy={force.isPending} onClick={() => force.mutate(false)}>K Data wins ✓ (undo decision)</Button>
          : <Button variant="secondary" busy={force.isPending} onClick={() => force.mutate(true)}>K Data wins</Button>)}
      </div>
      <ul>
        {rows.map((i) => <ItemRow key={i.id} item={i} runId={runId} />)}
      </ul>
    </li>
  );
}

function ItemRow({ item, runId }: { item: SimproItem; runId: number }) {
  const toast = useToast();
  const queryClient = useQueryClient();
  const copy = useMutation({
    mutationFn: () => api(`/api/simpro/items/${item.id}/copy-to-app`, { method: "POST" }),
    onSuccess: () => { queryClient.invalidateQueries({ queryKey: ["simpro", "run", runId] }); toast(`${item.fieldLabel} set to the Simpro value in K Data`); },
    onError: (e) => toast(e.message, "error"),
  });
  const empty = <span className="text-muted italic">empty</span>;
  const isEquipment = item.field === "(equipment)";
  return (
    <li className="grid gap-x-4 gap-y-1 border-b border-line px-4 py-2.5 last:border-0 md:grid-cols-[13rem_1fr_auto]">
      <span className="font-semibold text-ink">{item.fieldLabel}</span>
      <span className="min-w-0 break-words whitespace-pre-line">
        {!isEquipment && (item.outcome === "Changed in Simpro" || item.outcome === "Conflict" || item.outcome === "Gate goes back" ? (
          <>Simpro: <strong>{item.simproValue ?? empty}</strong> · K Data: <strong>{item.newValue ?? empty}</strong> · last synced: {item.lastSyncedValue ?? empty}</>
        ) : (
          <>{item.simproValue ?? empty} <span className="text-muted">→</span> <strong>{item.newValue ?? empty}</strong></>
        ))}
        {item.reason && <span className="block text-sm text-muted">{item.reason}</span>}
      </span>
      <span className="flex flex-wrap items-start gap-2">
        {item.canCopy && <Button variant="quiet" busy={copy.isPending} onClick={() => copy.mutate()}>Use Simpro value</Button>}
        <ItemOutcome outcome={item.outcome} />
      </span>
    </li>
  );
}

/** "K Data wins" for every held equipment at once (e.g. the first sync). The next sync writes them. */
function ForceAll({ runId, count }: { runId: number; count: number }) {
  const toast = useToast();
  const queryClient = useQueryClient();
  const [open, setOpen] = useState(false);
  const force = useMutation({
    mutationFn: () => api<{ forced: number }>(`/api/simpro/runs/${runId}/force-held`, { method: "POST" }),
    onSuccess: (r) => { setOpen(false); queryClient.invalidateQueries({ queryKey: ["simpro"] }); toast(`K Data wins for ${r.forced} equipment: written in the next sync`); },
    onError: (e) => toast(e.message, "error"),
  });
  return (
    <>
      <Button variant="secondary" onClick={() => setOpen(true)}>K Data wins for all {count}</Button>
      <Dialog open={open} title={`K Data wins for all ${count}?`} onClose={() => setOpen(false)}>
        <p>The next sync writes the K Data values of these {count} equipment to Simpro, including gates that go back. Use it when K Data is the reference, as in the first sync.</p>
        <p className="mt-2 text-muted">Each decision can still be removed one by one before the sync.</p>
        <div className="mt-5 flex justify-end gap-2">
          <Button variant="secondary" onClick={() => setOpen(false)}>Cancel</Button>
          <Button busy={force.isPending} onClick={() => force.mutate()}>K Data wins for all</Button>
        </div>
      </Dialog>
    </>
  );
}

