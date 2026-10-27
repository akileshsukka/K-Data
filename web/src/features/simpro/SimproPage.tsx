import { useEffect, useState } from "react";
import { Link, useNavigate } from "react-router";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api } from "../../api/client";
import type { SimproRun, SimproRunDetail, SimproSettings } from "../../api/types";
import { Button } from "../../components/Button";
import { Dialog } from "../../components/Dialog";
import { Field, TextInput } from "../../components/Field";
import { PageHeader } from "../../components/PageHeader";
import { formatDateTime } from "../../components/dates";
import { useToast } from "../../components/Toast";
import { AdminTabs } from "../admin/AdminTabs";
import { RunStatus } from "./SimproBits";

/** Push K Data into Simpro: preview, sync now, daily schedule and the history of runs. */
export function SimproPage() {
  const toast = useToast();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const [confirm, setConfirm] = useState(false);
  const settings = useQuery({ queryKey: ["simpro", "settings"], queryFn: ({ signal }) => api<SimproSettings>("/api/simpro/settings", { signal }) });
  const runs = useQuery({ queryKey: ["simpro", "runs"], queryFn: ({ signal }) => api<SimproRun[]>("/api/simpro/runs", { signal }) });

  const run = useMutation({
    mutationFn: (action: "preview" | "apply") => api<SimproRunDetail>(`/api/simpro/${action}`, { method: "POST" }),
    onSuccess: (d) => {
      setConfirm(false);
      queryClient.invalidateQueries({ queryKey: ["simpro"] });
      if (d.run.status === "Failed") toast(d.run.message ?? "The sync failed.", "error");
      navigate(`/admin/simpro/runs/${d.run.id}`);
    },
    onError: (e) => { setConfirm(false); toast(e.message, "error"); },
  });

  const s = settings.data;
  const last = runs.data?.find((r) => r.mode !== "Preview");
  return (
    <div>
      <AdminTabs />
      <PageHeader
        title="Simpro sync"
        description="K Data is the source; Simpro only shows the data. Equipment changed in Simpro, or on both sides, is held until you decide."
        actions={
          <>
            <Button variant="secondary" busy={run.isPending && run.variables === "preview"} disabled={!s?.configured || run.isPending} onClick={() => run.mutate("preview")}>Preview</Button>
            <Button disabled={!s?.configured || run.isPending} onClick={() => setConfirm(true)}>Sync now</Button>
          </>
        }
      />

      {s && !s.configured && (
        <div className="mt-6 max-w-3xl rounded-lg border border-ongoing/40 bg-ongoing/5 p-5">
          <h2 className="text-xl font-semibold">Simpro is not connected yet</h2>
          <p className="mt-1">Set <code className="rounded bg-surface px-1">ConnectionStrings:Simpro</code> in the server settings (database <strong>Preservation</strong>), then restart K Data.</p>
        </div>
      )}

      <div className="mt-6 grid gap-6 lg:grid-cols-[1fr_22rem]">
        <section className="rounded-lg border border-line bg-panel">
          <h2 className="px-5 pt-5 text-xl font-semibold">Runs</h2>
          {runs.data?.length === 0 && <p className="px-5 pt-2 pb-5 text-muted">No sync yet. Start with a preview: it shows everything and writes nothing.</p>}
          <ul className="mt-3">
            {runs.data?.map((r) => (
              <li key={r.id} className="border-t border-line">
                <Link to={`/admin/simpro/runs/${r.id}`} className="grid gap-1 px-5 py-3 hover:bg-surface sm:grid-cols-[1fr_auto] sm:items-center">
                  <span>
                    <span className="block font-semibold text-ink">
                      {r.mode === "Preview" ? "Preview" : r.mode === "Scheduled" ? "Daily sync" : r.mode === "Undo" ? `Undo of run ${r.undoOfRunId}` : "Sync"} · {formatDateTime(r.startedAtUtc)}
                    </span>
                    <span className="block text-sm text-muted">
                      {r.triggeredBy === "schedule" ? "Automatic" : r.triggeredBy}
                      {r.mode !== "Undo" && r.status !== "Failed" && ` · ${r.updatedCount} updated · ${r.createdCount} new · ${r.fieldCount} fields · ${r.heldCount} held`}
                    </span>
                  </span>
                  <RunStatus run={r} />
                </Link>
              </li>
            ))}
          </ul>
        </section>

        <div className="space-y-6">
          {last && (
            <section className="rounded-lg border border-line bg-panel p-5">
              <h2 className="text-xl font-semibold">Last sync</h2>
              <p className="mt-1 text-muted">{formatDateTime(last.startedAtUtc)}</p>
              <div className="mt-2"><RunStatus run={last} /></div>
              {last.heldCount > 0 && <p className="mt-2"><Link to={`/admin/simpro/runs/${last.id}?tab=held`} className="font-semibold text-violet">{last.heldCount} equipment waiting for a decision</Link></p>}
            </section>
          )}
          {s && <ScheduleCard settings={s} />}
        </div>
      </div>

      <Dialog open={confirm} title="Write to Simpro now?" onClose={() => setConfirm(false)}>
        <p>K Data compares again, writes every change that is not held and creates the new equipment, in one transaction. Held equipment are not touched.</p>
        <p className="mt-2 text-muted">You can undo the run afterwards, as long as nobody edits those equipment in Simpro meanwhile.</p>
        <div className="mt-5 flex justify-end gap-2">
          <Button variant="secondary" onClick={() => setConfirm(false)}>Cancel</Button>
          <Button busy={run.isPending && run.variables === "apply"} onClick={() => run.mutate("apply")}>Sync now</Button>
        </div>
      </Dialog>
    </div>
  );
}

function ScheduleCard({ settings }: { settings: SimproSettings }) {
  const toast = useToast();
  const queryClient = useQueryClient();
  const [enabled, setEnabled] = useState(settings.scheduleEnabled);
  const [time, setTime] = useState(settings.scheduleTime);
  useEffect(() => { setEnabled(settings.scheduleEnabled); setTime(settings.scheduleTime); }, [settings]);
  const save = useMutation({
    mutationFn: () => api<SimproSettings>("/api/simpro/settings", { method: "PUT", body: { scheduleEnabled: enabled, scheduleTime: time } }),
    onSuccess: (d) => { queryClient.setQueryData(["simpro", "settings"], d); toast(d.scheduleEnabled ? `Daily sync at ${d.scheduleTime} (Dubai)` : "Daily sync turned off"); },
    onError: (e) => toast(e.message, "error"),
  });
  const dirty = enabled !== settings.scheduleEnabled || time !== settings.scheduleTime;

  return (
    <section className="rounded-lg border border-line bg-panel p-5">
      <h2 className="text-xl font-semibold">Daily sync</h2>
      <p className="mt-1 text-sm text-muted">Writes everything that is not held and creates new equipment. Held equipment wait for you here.</p>
      <label className="mt-4 flex items-center gap-2 font-semibold">
        <input type="checkbox" checked={enabled} onChange={(e) => setEnabled(e.target.checked)} className="size-4 accent-violet" />
        Run every day
      </label>
      <div className="mt-3 flex items-end gap-3">
        <Field label="Time (Dubai)"><TextInput type="time" value={time} onChange={(e) => setTime(e.target.value)} className="w-32" /></Field>
        <Button busy={save.isPending} disabled={!dirty} onClick={() => save.mutate()}>Save</Button>
      </div>
      <p className="mt-3 text-sm text-muted">History user in Simpro: <strong>{settings.syncUsername}</strong></p>
      {settings.forcedCount > 0 && <p className="mt-1 text-sm text-muted">{settings.forcedCount} equipment set to “K Data wins” for the next sync.</p>}
    </section>
  );
}
