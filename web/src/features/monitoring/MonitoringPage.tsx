import { useMemo, useState } from "react";
import { Link, useParams, useSearchParams } from "react-router";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api } from "../../api/client";
import { Perm, type Group, type MonitoringRow, type MonitoringSettings, type MonitoringView, type RoundResult } from "../../api/types";
import { useMeasurements, useUnit } from "../../app/useCatalog";
import { useSession } from "../../app/session";
import { Button } from "../../components/Button";
import { SelectInput, TextInput } from "../../components/Field";
import { CurrentGateChip } from "../../components/GateStatus";
import { PageHeader } from "../../components/PageHeader";
import { useToast } from "../../components/Toast";
import { dubaiLocalToUtc, formatDate, formatDateTime, nowDubaiLocal } from "../../components/dates";
import { useDebounced } from "../../components/useDebounced";
import { MonitoringStatusChip } from "./MonitoringStatusChip";

const typeLabel = { Equipment: "Equipment", Pipeline: "Line", ElectricalInstrument: "E&I" } as const;

/**
 * N2 blanket monitoring of a unit: DURING (not marked as preserved yet) and POST (marked).
 * POST assets are due when their last N2 reading is older than the interval. A round records many readings at once.
 */
export function MonitoringPage() {
  const unitId = Number(useParams().unitId);
  const { unit } = useUnit(unitId);
  const session = useSession();
  const [params, setParams] = useSearchParams();
  const groupId = params.get("group") ?? "";
  const status = params.get("status") ?? "";
  const [phase, setPhase] = useState("");
  const [type, setType] = useState("");
  const [text, setText] = useState("");
  const search = useDebounced(text.trim().toLowerCase(), 250);
  const [round, setRound] = useState(false);

  const view = useQuery({
    queryKey: ["monitoring", unitId, groupId],
    queryFn: ({ signal }) => api<MonitoringView>(`/api/monitoring?unitId=${unitId}${groupId ? `&groupId=${groupId}` : ""}`, { signal }),
  });
  const groups = useQuery({ queryKey: ["groups", unitId], queryFn: ({ signal }) => api<Group[]>(`/api/groups?unitId=${unitId}`, { signal }) });

  const set = (key: string, value: string) => {
    const next = new URLSearchParams(params);
    if (value) next.set(key, value); else next.delete(key);
    setParams(next, { replace: true });
  };

  const all = view.data?.rows ?? [];
  const rows = useMemo(() => all.filter((r) =>
    (!status || (status === "Low N2" ? r.lowN2 : r.status === status)) &&
    (!phase || r.phase === phase) &&
    (!type || r.type === type) &&
    (!search || `${r.tag ?? ""} ${r.code} ${r.description ?? ""}`.toLowerCase().includes(search))), [all, status, phase, type, search]);

  const s = view.data?.summary;
  const settings = view.data?.settings;
  const canRound = session.can(Perm.readingsWrite);

  return (
    <div>
      <PageHeader
        title="Monitoring"
        description={unit ? `${unit.name} — N2 blanket checks. Post-preservation assets are due every ${settings?.intervalDays ?? "…"} days${settings?.minN2Barg != null ? `; below ${settings.minN2Barg} barg is low` : ""}.` : undefined}
        actions={canRound && !round ? <Button onClick={() => setRound(true)}>Record a round</Button> : undefined}
      />

      {s && (
        <section className="mt-5 grid grid-cols-2 gap-3 md:grid-cols-3 xl:grid-cols-6" aria-label="Summary">
          <Tile label="Post-preservation" value={s.post} detail="Marked as preserved" active={phase === "Post"} onClick={() => setPhase(phase === "Post" ? "" : "Post")} />
          <Tile label="During preservation" value={s.during} detail="Not marked yet" active={phase === "During"} onClick={() => setPhase(phase === "During" ? "" : "During")} />
          <Tile label="Overdue" value={s.overdue} detail={`N2 older than ${settings?.intervalDays} days`} tone="hold" active={status === "Overdue"} onClick={() => set("status", status === "Overdue" ? "" : "Overdue")} />
          <Tile label="No reading" value={s.noReading} detail="Nothing since preserved" tone="hold" active={status === "No reading since preserved"} onClick={() => set("status", status === "No reading since preserved" ? "" : "No reading since preserved")} />
          <Tile label="Low N2" value={s.lowN2} detail={settings?.minN2Barg != null ? `Below ${settings.minN2Barg} barg` : "Set a minimum below"} tone="ongoing" active={status === "Low N2"} onClick={() => set("status", status === "Low N2" ? "" : "Low N2")} />
          <Tile label="Up to date" value={s.ok} detail="Checked in time" tone="done" active={status === "OK"} onClick={() => set("status", status === "OK" ? "" : "OK")} />
        </section>
      )}

      <div className="mt-5 grid gap-2 sm:grid-cols-2 lg:grid-cols-[minmax(14rem,2fr)_repeat(4,minmax(9rem,1fr))]">
        <TextInput type="search" placeholder="Search tag, ID or description" value={text} onChange={(e) => setText(e.target.value)} aria-label="Search" className="mt-0" />
        <SelectInput value={status} onChange={(e) => set("status", e.target.value)} aria-label="Status" className="mt-0">
          <option value="">Any status</option>
          {["Overdue", "No reading since preserved", "Low N2", "OK", "During preservation", "Removed from site"].map((x) => <option key={x}>{x}</option>)}
        </SelectInput>
        <SelectInput value={phase} onChange={(e) => setPhase(e.target.value)} aria-label="Phase" className="mt-0">
          <option value="">During and post</option>
          <option value="During">During preservation</option>
          <option value="Post">Post-preservation</option>
        </SelectInput>
        <SelectInput value={type} onChange={(e) => setType(e.target.value)} aria-label="Asset type" className="mt-0">
          <option value="">Equipment, lines and E&I</option>
          <option value="Equipment">Equipment</option>
          <option value="Pipeline">Lines</option>
          <option value="ElectricalInstrument">E&I</option>
        </SelectInput>
        <SelectInput value={groupId} onChange={(e) => set("group", e.target.value)} aria-label="Workpack or group" className="mt-0">
          <option value="">Any workpack or group</option>
          {(groups.data ?? []).map((g) => <option key={g.id} value={g.id}>{g.code} — {g.name}</option>)}
        </SelectInput>
      </div>

      {view.isPending && <p className="mt-6 text-muted">Loading…</p>}
      {view.error && <p className="mt-6 text-hold">{view.error.message}</p>}
      {view.data && rows.length === 0 && <p className="mt-6 text-muted">Nothing matches these filters.</p>}

      {round && view.data ? (
        <RoundForm rows={rows} onClose={() => setRound(false)} unitId={unitId} />
      ) : (
        rows.length > 0 && <MonitoringTable rows={rows} />
      )}

      {session.can(Perm.catalogManage) && settings && <SettingsCard settings={settings} />}
    </div>
  );
}

function Tile({ label, value, detail, tone, active, onClick }: { label: string; value: number; detail: string; tone?: "hold" | "ongoing" | "done"; active: boolean; onClick: () => void }) {
  const accent = tone === "hold" && value > 0 ? "text-hold" : tone === "ongoing" && value > 0 ? "text-ongoing" : tone === "done" ? "text-done" : "text-ink";
  return (
    <button type="button" onClick={onClick} aria-pressed={active}
      className={`rounded-lg border bg-panel p-4 text-left transition-colors hover:border-violet ${active ? "border-violet ring-2 ring-violet/30" : "border-line"}`}>
      <span className="block text-sm font-semibold text-muted">{label}</span>
      <span className={`block font-display text-[2rem] leading-none font-bold tabular-nums ${accent}`}>{value}</span>
      <span className="mt-1 block text-sm text-muted">{detail}</span>
    </button>
  );
}

function MonitoringTable({ rows }: { rows: MonitoringRow[] }) {
  return (
    <>
      <div className="mt-4 hidden overflow-x-auto rounded-lg border border-line bg-panel md:block">
        <table className="w-full border-separate border-spacing-0 text-left text-[15px]">
          <thead className="font-display text-muted">
            <tr>
              <th className="border-b border-line px-3 py-2.5 font-semibold">Tag</th>
              <th className="border-b border-line px-3 py-2.5 font-semibold">Status</th>
              <th className="border-b border-line px-3 py-2.5 font-semibold">Preserved</th>
              <th className="border-b border-line px-3 py-2.5 text-right font-semibold">N2 blanket</th>
              <th className="border-b border-line px-3 py-2.5 font-semibold">Last N2 reading</th>
              <th className="border-b border-line px-3 py-2.5 text-right font-semibold">Pressure</th>
              <th className="border-b border-line px-3 py-2.5 text-right font-semibold">O2</th>
              <th className="border-b border-line px-3 py-2.5 font-semibold">Current Gate</th>
              <th className="border-b border-line px-3 py-2.5 font-semibold">Workpack</th>
            </tr>
          </thead>
          <tbody>
            {rows.map((r) => (
              <tr key={r.id} className="group hover:bg-violet-soft/50">
                <td className="border-b border-line px-3 py-2">
                  <Link to={`/assets/${r.id}?tab=readings`} className="font-semibold whitespace-nowrap text-ink group-hover:text-violet">{r.tag ?? r.code}</Link>
                  <span className="block text-sm text-muted">{r.code} · {typeLabel[r.type]}</span>
                </td>
                <td className="border-b border-line px-3 py-2"><MonitoringStatusChip status={r.status} lowN2={r.lowN2} /></td>
                <td className="border-b border-line px-3 py-2 whitespace-nowrap">{r.preservedAtUtc ? formatDate(r.preservedAtUtc) : <span className="text-muted">—</span>}</td>
                <td className={`border-b border-line px-3 py-2 text-right tabular-nums ${r.lowN2 ? "font-semibold text-ongoing" : ""}`}>{r.n2Barg ?? "—"}{r.n2Barg != null && <span className="text-sm text-muted"> barg</span>}</td>
                <td className="border-b border-line px-3 py-2 whitespace-nowrap">
                  {r.n2AtUtc ? <>{formatDateTime(r.n2AtUtc)}<span className="block text-sm text-muted">{r.daysSinceN2 === 0 ? "today" : `${r.daysSinceN2} day${r.daysSinceN2 === 1 ? "" : "s"} ago`}</span></> : <span className="text-muted">Never</span>}
                </td>
                <td className="border-b border-line px-3 py-2 text-right tabular-nums">{r.pressureBarg ?? "—"}</td>
                <td className="border-b border-line px-3 py-2 text-right tabular-nums">{r.o2Pct ?? "—"}</td>
                <td className="border-b border-line px-3 py-2"><CurrentGateChip value={r.currentGate} /></td>
                <td className="border-b border-line px-3 py-2 text-sm">{r.groups.join(", ")}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      <ul className="mt-4 space-y-2 md:hidden">
        {rows.map((r) => (
          <li key={r.id}>
            <Link to={`/assets/${r.id}?tab=readings`} className="block rounded-lg border border-line bg-panel p-3">
              <div className="flex items-start justify-between gap-3">
                <div className="min-w-0">
                  <p className="truncate font-semibold text-ink">{r.tag ?? r.code}</p>
                  <p className="text-sm text-muted">N2 {r.n2Barg ?? "—"} barg · {r.n2AtUtc ? `${r.daysSinceN2} days ago` : "never measured"}</p>
                </div>
                <MonitoringStatusChip status={r.status} lowN2={r.lowN2} />
              </div>
            </Link>
          </li>
        ))}
      </ul>
    </>
  );
}

/** One measurement time for everyone; only the filled cells are saved, all together or nothing. */
function RoundForm({ rows, onClose, unitId }: { rows: MonitoringRow[]; onClose: () => void; unitId: number }) {
  const measurements = useMeasurements();
  const toast = useToast();
  const client = useQueryClient();
  const all = measurements.data ?? [];
  const [chosen, setChosen] = useState<string[]>(["G4N2BlanketBarg", "PressureBarg"]);
  const [measuredAt, setMeasuredAt] = useState(nowDubaiLocal);
  const [note, setNote] = useState("");
  const [values, setValues] = useState<Record<string, string>>({});
  const [result, setResult] = useState<RoundResult>();
  const columns = all.filter((m) => chosen.includes(m.key));
  const key = (assetId: string, mId: number) => `${assetId}:${mId}`;
  const filled = Object.entries(values).filter(([, v]) => v.trim() !== "");
  const bad = filled.filter(([, v]) => Number.isNaN(Number(v.replace(",", "."))));

  const save = useMutation({
    mutationFn: () => {
      const byAsset = new Map<string, { measurementTypeId: number; value: number }[]>();
      for (const [k, v] of filled) {
        const [assetId, mId] = k.split(":");
        byAsset.set(assetId, [...(byAsset.get(assetId) ?? []), { measurementTypeId: Number(mId), value: Number(v.replace(",", ".")) }]);
      }
      return api<RoundResult>("/api/monitoring/rounds", {
        method: "POST",
        body: { measuredAtUtc: dubaiLocalToUtc(measuredAt), note: note.trim() || null, rows: [...byAsset].map(([assetId, vs]) => ({ assetId, values: vs })) },
      });
    },
    onSuccess: (r) => {
      setResult(r);
      setValues({});
      client.invalidateQueries({ queryKey: ["monitoring", unitId] });
      toast(`${r.readings} reading${r.readings === 1 ? "" : "s"} saved for ${r.assets} asset${r.assets === 1 ? "" : "s"}`);
    },
    onError: (e) => toast(e.message, "error"),
  });

  return (
    <section className="mt-5 rounded-lg border border-violet/40 bg-panel" aria-label="Monitoring round">
      <div className="flex flex-wrap items-end gap-3 border-b border-line p-4">
        <label>
          <span className="block text-sm font-semibold text-ink">Measured at (Dubai time)</span>
          <TextInput type="datetime-local" value={measuredAt} onChange={(e) => setMeasuredAt(e.target.value)} className="mt-1" />
        </label>
        <label className="min-w-[14rem] flex-1">
          <span className="block text-sm font-semibold text-ink">Note for all readings (optional)</span>
          <TextInput value={note} maxLength={4000} onChange={(e) => setNote(e.target.value)} className="mt-1" />
        </label>
        <fieldset className="flex flex-wrap gap-x-4 gap-y-1">
          <legend className="text-sm font-semibold text-ink">Readings to enter</legend>
          {all.map((m) => (
            <label key={m.key} className="inline-flex items-center gap-1.5 text-[15px]">
              <input type="checkbox" className="size-4 accent-violet" checked={chosen.includes(m.key)}
                onChange={(e) => setChosen(e.target.checked ? [...chosen, m.key] : chosen.filter((k) => k !== m.key))} />
              {m.label}
            </label>
          ))}
        </fieldset>
      </div>

      {result && result.proposals.length > 0 && (
        <div className="border-b border-line bg-ongoing/5 px-4 py-3 text-sm">
          <strong>{result.proposals.length} asset{result.proposals.length === 1 ? "" : "s"} have a proposed change to review:</strong>{" "}
          {result.proposals.map((p, i) => <span key={p.assetId}>{i > 0 && ", "}<Link className="font-semibold text-violet" to={`/assets/${p.assetId}`}>{p.tag ?? p.code}</Link></span>)}
        </div>
      )}

      <div className="max-h-[60vh] overflow-auto">
        <table className="w-full border-separate border-spacing-0 text-left text-[15px]">
          <thead className="sticky top-0 z-10 bg-panel font-display text-muted">
            <tr>
              <th className="border-b border-line px-3 py-2 font-semibold">Tag</th>
              <th className="border-b border-line px-3 py-2 font-semibold">Last N2</th>
              {columns.map((m) => <th key={m.id} className="border-b border-line px-2 py-2 font-semibold">{m.label}</th>)}
            </tr>
          </thead>
          <tbody>
            {rows.filter((r) => r.status !== "Removed from site").map((r) => (
              <tr key={r.id}>
                <td className="border-b border-line px-3 py-1.5">
                  <span className="font-semibold whitespace-nowrap text-ink">{r.tag ?? r.code}</span>
                  <span className="block text-sm text-muted">{r.phase === "Post" ? "Post" : "During"} · {r.status === "OK" ? "up to date" : r.status.toLowerCase()}</span>
                </td>
                <td className="border-b border-line px-3 py-1.5 text-sm whitespace-nowrap text-muted">{r.n2Barg != null ? `${r.n2Barg} barg · ${r.daysSinceN2} d` : "—"}</td>
                {columns.map((m) => {
                  const k = key(r.id, m.id);
                  const v = values[k] ?? "";
                  const invalid = v.trim() !== "" && Number.isNaN(Number(v.replace(",", ".")));
                  return (
                    <td key={m.id} className="border-b border-line px-2 py-1.5">
                      <input inputMode="decimal" value={v} aria-label={`${m.label} of ${r.tag ?? r.code}`} aria-invalid={invalid}
                        onChange={(e) => setValues({ ...values, [k]: e.target.value })}
                        className={`h-9 w-24 rounded-md border bg-panel px-2 text-right tabular-nums focus:border-violet focus:outline-none ${invalid ? "border-hold" : "border-line"}`} />
                    </td>
                  );
                })}
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      <div className="flex flex-wrap items-center justify-end gap-2 border-t border-line p-4">
        <span className="mr-auto text-sm text-muted">{bad.length > 0 ? <span className="text-hold">{bad.length} value{bad.length === 1 ? " is" : "s are"} not a number.</span> : `${filled.length} reading${filled.length === 1 ? "" : "s"} entered. All are saved together, or none if one is wrong.`}</span>
        <Button variant="secondary" onClick={onClose}>Close the round</Button>
        <Button busy={save.isPending} disabled={filled.length === 0 || bad.length > 0} onClick={() => save.mutate()}>Save {filled.length || ""} reading{filled.length === 1 ? "" : "s"}</Button>
      </div>
    </section>
  );
}

function SettingsCard({ settings }: { settings: MonitoringSettings }) {
  const toast = useToast();
  const client = useQueryClient();
  const [days, setDays] = useState(String(settings.intervalDays));
  const [min, setMin] = useState(settings.minN2Barg?.toString() ?? "");
  const save = useMutation({
    mutationFn: () => api<MonitoringSettings>("/api/settings/monitoring", { method: "PUT", body: { intervalDays: Number(days), minN2Barg: min.trim() === "" ? null : Number(min.replace(",", ".")) } }),
    onSuccess: () => { client.invalidateQueries({ queryKey: ["monitoring"] }); toast("Monitoring settings saved"); },
    onError: (e) => toast(e.message, "error"),
  });
  return (
    <details className="mt-8 max-w-2xl rounded-lg border border-line bg-panel p-4">
      <summary className="cursor-pointer font-display text-lg font-semibold text-ink">Monitoring settings (all units)</summary>
      <form className="mt-3 flex flex-wrap items-end gap-3" onSubmit={(e) => { e.preventDefault(); save.mutate(); }}>
        <label>
          <span className="block text-sm font-semibold text-ink">Check N2 every (days)</span>
          <TextInput inputMode="numeric" value={days} onChange={(e) => setDays(e.target.value)} className="mt-1 w-32" />
        </label>
        <label>
          <span className="block text-sm font-semibold text-ink">Low N2 below (barg)</span>
          <TextInput inputMode="decimal" value={min} placeholder="Off" onChange={(e) => setMin(e.target.value)} className="mt-1 w-32" />
        </label>
        <Button type="submit" busy={save.isPending}>Save</Button>
      </form>
      <p className="mt-2 text-sm text-muted">Leave the minimum empty until engineering confirms the value; nothing is flagged as low meanwhile.</p>
    </details>
  );
}
