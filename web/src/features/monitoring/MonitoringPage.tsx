import { useMemo, useRef, useState, type ClipboardEvent } from "react";
import { Link, useNavigate, useParams, useSearchParams } from "react-router";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api, download, upload } from "../../api/client";
import { Perm, type Group, type ImportRunDetail, type MonitoringRow, type MonitoringSettings, type MonitoringView, type RoundResult } from "../../api/types";
import { useMeasurements, useUnit } from "../../app/useCatalog";
import { useSession } from "../../app/session";
import { Button } from "../../components/Button";
import { SelectInput, TextInput } from "../../components/Field";
import { CurrentGateChip } from "../../components/GateStatus";
import { PageHeader } from "../../components/PageHeader";
import { TagListFilter, tagKey, tagList } from "../../components/TagListFilter";
import { useToast } from "../../components/Toast";
import { dubaiLocalToUtc, formatDate, formatDateTime, nowDubaiLocal } from "../../components/dates";
import { useDebounced } from "../../components/useDebounced";
import { MonitoringStatusChip } from "./MonitoringStatusChip";

const typeLabel = { Equipment: "Equipment", Pipeline: "Line", ElectricalInstrument: "E&I" } as const;
const N2_KEY = "G4N2BlanketBarg";
const PRESSURE_KEY = "PressureBarg";
const LOW = "Low pressure";

const ago = (days: number | null) => (days == null ? "" : days === 0 ? "today" : `${days} day${days === 1 ? "" : "s"} ago`);

/**
 * Preservation monitoring of a unit.
 * DURING (not marked as preserved): the measured pressure is kept as G4 - N2 Blanket, without due dates or alerts.
 * POST (marked): it is kept as Pressure (barg); the asset is due when its last Pressure reading is older than the interval,
 * and low below the minimum. A round records many readings at once; the Excel template loads them in bulk.
 */
export function MonitoringPage() {
  const unitId = Number(useParams().unitId);
  const { unit } = useUnit(unitId);
  const session = useSession();
  const [params, setParams] = useSearchParams();
  const groupId = params.get("group") ?? "";
  const status = params.get("status") ?? "";
  const tags = params.get("tags") ?? "";
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
  // Pasted tags: only those rows, in the pasted order, so a column of values from the same report can be pasted next to them.
  const pasted = useMemo(() => new Map(tagList(tags).map((t, i) => [t, i])), [tags]);
  const position = (r: MonitoringRow) => Math.min(pasted.get(tagKey(r.tag ?? "")) ?? Infinity, pasted.get(tagKey(r.code)) ?? Infinity);
  const notFound = [...pasted.keys()].filter((t) => !all.some((r) => tagKey(r.tag ?? "") === t || tagKey(r.code) === t));
  const rows = useMemo(() => {
    const shown = all.filter((r) =>
      (!status || (status === LOW ? r.lowPressure : r.status === status)) &&
      (!phase || r.phase === phase) &&
      (!type || r.type === type) &&
      (pasted.size === 0 || position(r) !== Infinity) &&
      (!search || `${r.tag ?? ""} ${r.code} ${r.description ?? ""}`.toLowerCase().includes(search)));
    return pasted.size === 0 ? shown : [...shown].sort((a, b) => position(a) - position(b));
  }, [all, status, phase, type, search, pasted]);

  const s = view.data?.summary;
  const settings = view.data?.settings;
  const canRound = session.can(Perm.readingsWrite);
  const canUpload = canRound && session.can(Perm.excelImport);

  return (
    <div>
      <PageHeader
        title="Monitoring"
        description={unit ? `${unit.name} — while an asset is being preserved its pressure is kept as G4 N2 blanket. Once it is marked as preserved, it is kept as Pressure and is due every ${settings?.intervalDays ?? "…"} days${settings?.minPressureBarg != null ? `; below ${settings.minPressureBarg} barg is low` : ""}.` : undefined}
        actions={canRound && !round ? (
          <>
            <ExcelReadings unitId={unitId} canUpload={canUpload} />
            <Button onClick={() => setRound(true)}>Record a round</Button>
          </>
        ) : undefined}
      />

      {s && (
        <section className="mt-5 grid grid-cols-2 gap-3 md:grid-cols-3 xl:grid-cols-6" aria-label="Summary">
          <Tile label="Post-preservation" value={s.post} detail="Marked as preserved" active={phase === "Post"} onClick={() => setPhase(phase === "Post" ? "" : "Post")} />
          <Tile label="During preservation" value={s.during} detail="Not marked yet" active={phase === "During"} onClick={() => setPhase(phase === "During" ? "" : "During")} />
          <Tile label="Overdue" value={s.overdue} detail={`Pressure older than ${settings?.intervalDays} days`} tone="hold" active={status === "Overdue"} onClick={() => set("status", status === "Overdue" ? "" : "Overdue")} />
          <Tile label="No reading" value={s.noReading} detail="No pressure since preserved" tone="hold" active={status === "No reading since preserved"} onClick={() => set("status", status === "No reading since preserved" ? "" : "No reading since preserved")} />
          <Tile label={LOW} value={s.lowPressure} detail={settings?.minPressureBarg != null ? `Below ${settings.minPressureBarg} barg` : "Set a minimum below"} tone="ongoing" active={status === LOW} onClick={() => set("status", status === LOW ? "" : LOW)} />
          <Tile label="Up to date" value={s.ok} detail="Checked in time" tone="done" active={status === "OK"} onClick={() => set("status", status === "OK" ? "" : "OK")} />
        </section>
      )}

      <div className="mt-5 grid gap-2 sm:grid-cols-2 lg:grid-cols-[minmax(14rem,2fr)_repeat(4,minmax(9rem,1fr))_auto]">
        <TextInput type="search" placeholder="Search tag, ID or description" value={text} onChange={(e) => setText(e.target.value)} aria-label="Search" className="mt-0" />
        <SelectInput value={status} onChange={(e) => set("status", e.target.value)} aria-label="Status" className="mt-0">
          <option value="">Any status</option>
          {["Overdue", "No reading since preserved", LOW, "OK", "During preservation", "Removed from site"].map((x) => <option key={x}>{x}</option>)}
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
        <TagListFilter value={tags} onChange={(v) => set("tags", v)} />
      </div>

      {pasted.size > 0 && view.data && (
        <p className="mt-2 text-sm text-muted">
          Pasted tags: {pasted.size - notFound.length} of {pasted.size} found, shown in the pasted order
          {notFound.length > 0 && <> · <span className="text-hold">not found: {notFound.slice(0, 15).join(", ").toUpperCase()}{notFound.length > 15 ? ` and ${notFound.length - 15} more` : ""}</span></>}
          {" · "}<button type="button" className="font-semibold text-violet" onClick={() => set("tags", "")}>Remove this filter</button>
        </p>
      )}

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

/** The empty readings template (downloaded once, reused) and its upload, previewed before anything is saved. */
function ExcelReadings({ unitId, canUpload }: { unitId: number; canUpload: boolean }) {
  const toast = useToast();
  const navigate = useNavigate();
  const input = useRef<HTMLInputElement>(null);
  const [busy, setBusy] = useState<"template" | "upload" | null>(null);

  async function template() {
    setBusy("template");
    try { toast(`${await download(`/api/units/${unitId}/monitoring/readings-template`)} downloaded`); }
    catch (e) { toast((e as Error).message, "error"); }
    finally { setBusy(null); }
  }

  async function send(file: File | undefined) {
    if (input.current) input.current.value = "";
    if (!file) return;
    setBusy("upload");
    try {
      const detail = await upload<ImportRunDetail>(`/api/units/${unitId}/monitoring/readings-import`, file);
      navigate(`/imports/${detail.run.id}`);
    } catch (e) {
      toast((e as Error).message, "error");
    } finally {
      setBusy(null);
    }
  }

  return (
    <>
      <Button variant="secondary" busy={busy === "template"} onClick={template}>Readings template</Button>
      {canUpload && (
        <>
          <input ref={input} type="file" accept=".xlsx" className="hidden" onChange={(e) => send(e.target.files?.[0])} aria-label="Readings file" />
          <Button variant="secondary" busy={busy === "upload"} onClick={() => input.current?.click()}>Upload readings</Button>
        </>
      )}
    </>
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

/** Last reading that matters for the row: Pressure once POST (with its age), the N2 blanket while DURING. */
function LastReading({ r }: { r: MonitoringRow }) {
  if (r.phase === "Post") {
    if (r.daysSincePressure == null) return <span className="text-muted">None since preserved</span>;
    return <>{formatDateTime(r.pressureAtUtc)}<span className="block text-sm text-muted">Pressure · {ago(r.daysSincePressure)}</span></>;
  }
  return r.n2AtUtc ? <>{formatDateTime(r.n2AtUtc)}<span className="block text-sm text-muted">N2 blanket</span></> : <span className="text-muted">Never</span>;
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
              <th className="border-b border-line px-3 py-2.5 text-right font-semibold">Pressure</th>
              <th className="border-b border-line px-3 py-2.5 font-semibold">Last reading</th>
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
                <td className="border-b border-line px-3 py-2"><MonitoringStatusChip status={r.status} lowPressure={r.lowPressure} /></td>
                <td className="border-b border-line px-3 py-2 whitespace-nowrap">{r.preservedAtUtc ? formatDate(r.preservedAtUtc) : <span className="text-muted">—</span>}</td>
                <td className="border-b border-line px-3 py-2 text-right tabular-nums">{r.n2Barg ?? "—"}{r.n2Barg != null && <span className="text-sm text-muted"> barg</span>}</td>
                <td className={`border-b border-line px-3 py-2 text-right tabular-nums ${r.lowPressure ? "font-semibold text-ongoing" : ""}`}>{r.pressureBarg ?? "—"}{r.pressureBarg != null && <span className="text-sm text-muted"> barg</span>}</td>
                <td className="border-b border-line px-3 py-2 whitespace-nowrap"><LastReading r={r} /></td>
                <td className="border-b border-line px-3 py-2 text-right tabular-nums">{r.o2Pct ?? "—"}</td>
                <td className="border-b border-line px-3 py-2"><CurrentGateChip value={r.currentGate} type={r.type} /></td>
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
                  <p className="text-sm text-muted">
                    {r.phase === "Post"
                      ? `Pressure ${r.pressureBarg ?? "—"} barg · ${r.daysSincePressure != null ? ago(r.daysSincePressure) : "none since preserved"}`
                      : `N2 ${r.n2Barg ?? "—"} barg · ${r.n2AtUtc ? formatDate(r.n2AtUtc) : "never measured"}`}
                  </p>
                </div>
                <MonitoringStatusChip status={r.status} lowPressure={r.lowPressure} />
              </div>
            </Link>
          </li>
        ))}
      </ul>
    </>
  );
}

/** Column of the round: the pressure (saved as N2 blanket or Pressure by phase) or another measurement. */
type Column = { id: string; label: string; measurementId?: number };
const PRESSURE: Column = { id: "p", label: "Pressure (barg)" };
const parse = (v: string) => Number(v.trim().replace(",", "."));

/**
 * One measurement time for everyone; only the filled cells are saved, all together or nothing.
 * A column copied from Excel can be pasted in a cell: it fills that column downwards (and the next ones, for several columns).
 */
function RoundForm({ rows, onClose, unitId }: { rows: MonitoringRow[]; onClose: () => void; unitId: number }) {
  const measurements = useMeasurements();
  const toast = useToast();
  const client = useQueryClient();
  const others = (measurements.data ?? []).filter((m) => m.key !== N2_KEY && m.key !== PRESSURE_KEY);
  const [chosen, setChosen] = useState<string[]>([]);
  const [measuredAt, setMeasuredAt] = useState(nowDubaiLocal);
  const [note, setNote] = useState("");
  const [values, setValues] = useState<Record<string, string>>({});
  const [result, setResult] = useState<RoundResult>();
  const columns: Column[] = [PRESSURE, ...others.filter((m) => chosen.includes(m.key)).map((m) => ({ id: String(m.id), label: m.label, measurementId: m.id }))];
  const visible = rows.filter((r) => r.status !== "Removed from site");
  const key = (assetId: string, col: string) => `${assetId}:${col}`;
  const filled = Object.entries(values).filter(([k, v]) => v.trim() !== "" && columns.some((c) => k.endsWith(`:${c.id}`)));
  const bad = filled.filter(([, v]) => Number.isNaN(parse(v)));
  const zeros = filled.filter(([, v]) => !Number.isNaN(parse(v)) && parse(v) === 0);
  const measuredUtc = measuredAt ? dubaiLocalToUtc(measuredAt) : "";
  // Same rule as the server: preserved at the measurement time → Pressure (barg), otherwise G4 - N2 Blanket.
  const isPost = (r: MonitoringRow) => !!r.preservedAtUtc && !!measuredUtc && new Date(measuredUtc) >= new Date(r.preservedAtUtc);

  function paste(e: ClipboardEvent<HTMLInputElement>, rowIndex: number, colIndex: number) {
    const text = e.clipboardData.getData("text");
    if (!/[\n\t]/.test(text.trim())) return;
    e.preventDefault();
    const lines = text.replace(/\r/g, "").replace(/\n$/, "").split("\n");
    const next = { ...values };
    lines.forEach((line, i) => {
      const r = visible[rowIndex + i];
      if (!r) return;
      line.split("\t").forEach((cell, j) => {
        const c = columns[colIndex + j];
        if (c) next[key(r.id, c.id)] = cell.trim();
      });
    });
    setValues(next);
    const extra = rowIndex + lines.length - visible.length;
    toast(extra > 0 ? `${lines.length - extra} values pasted; ${extra} more lines than rows were left out` : `${lines.length} lines pasted`, extra > 0 ? "error" : "info");
  }

  const save = useMutation({
    mutationFn: () => {
      const byAsset = new Map<string, { pressure?: number; values: { measurementTypeId: number; value: number }[] }>();
      for (const [k, v] of filled) {
        const [assetId, col] = k.split(":");
        const entry = byAsset.get(assetId) ?? { values: [] };
        if (col === PRESSURE.id) entry.pressure = parse(v);
        else entry.values.push({ measurementTypeId: Number(col), value: parse(v) });
        byAsset.set(assetId, entry);
      }
      return api<RoundResult>("/api/monitoring/rounds", {
        method: "POST",
        body: { measuredAtUtc: measuredUtc, note: note.trim() || null, rows: [...byAsset].map(([assetId, e]) => ({ assetId, ...e })) },
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
          <legend className="text-sm font-semibold text-ink">Other readings to enter</legend>
          {others.map((m) => (
            <label key={m.key} className="inline-flex items-center gap-1.5 text-[15px]">
              <input type="checkbox" className="size-4 accent-violet" checked={chosen.includes(m.key)}
                onChange={(e) => setChosen(e.target.checked ? [...chosen, m.key] : chosen.filter((k) => k !== m.key))} />
              {m.label}
            </label>
          ))}
        </fieldset>
        <p className="w-full text-sm text-muted">
          Pressure is saved as <strong>G4 N2 blanket</strong> for assets not marked as preserved at that time, and as <strong>Pressure (barg)</strong> for preserved ones.
          To fill a column, copy it in Excel and paste it in its first cell.
        </p>
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
              <th className="border-b border-line px-3 py-2 font-semibold">Last</th>
              {columns.map((c) => <th key={c.id} className="border-b border-line px-2 py-2 font-semibold">{c.label}</th>)}
            </tr>
          </thead>
          <tbody>
            {visible.map((r, rowIndex) => {
              const post = isPost(r);
              return (
                <tr key={r.id}>
                  <td className="border-b border-line px-3 py-1.5">
                    <span className="font-semibold whitespace-nowrap text-ink">{r.tag ?? r.code}</span>
                    <span className="block text-sm text-muted">{post ? "Post · saved as Pressure" : "During · saved as N2 blanket"}</span>
                  </td>
                  <td className="border-b border-line px-3 py-1.5 text-sm whitespace-nowrap text-muted">
                    {post ? (r.pressureBarg != null ? `${r.pressureBarg} barg${r.daysSincePressure != null ? ` · ${r.daysSincePressure} d` : ""}` : "—")
                      : (r.n2Barg != null ? `N2 ${r.n2Barg} barg` : "—")}
                  </td>
                  {columns.map((c, colIndex) => {
                    const k = key(r.id, c.id);
                    const v = values[k] ?? "";
                    const invalid = v.trim() !== "" && Number.isNaN(parse(v));
                    const zero = !invalid && v.trim() !== "" && parse(v) === 0;
                    return (
                      <td key={c.id} className="border-b border-line px-2 py-1.5">
                        <input inputMode="decimal" value={v} aria-label={`${c.label} of ${r.tag ?? r.code}`} aria-invalid={invalid}
                          onChange={(e) => setValues({ ...values, [k]: e.target.value })}
                          onPaste={(e) => paste(e, rowIndex, colIndex)}
                          className={`h-9 w-24 rounded-md border bg-panel px-2 text-right tabular-nums focus:border-violet focus:outline-none ${invalid ? "border-hold" : zero ? "border-ongoing" : "border-line"}`} />
                      </td>
                    );
                  })}
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>

      <div className="flex flex-wrap items-center justify-end gap-2 border-t border-line p-4">
        <span className="mr-auto text-sm text-muted">
          {bad.length > 0 ? <span className="text-hold">{bad.length} value{bad.length === 1 ? " is" : "s are"} not a number.</span>
            : <>{filled.length} reading{filled.length === 1 ? "" : "s"} entered. All are saved together, or none if one is wrong.
              {zeros.length > 0 && <span className="text-ongoing"> {zeros.length} {zeros.length === 1 ? "is" : "are"} 0: {zeros.length === 1 ? "it is" : "they are"} saved too, check {zeros.length === 1 ? "it" : "them"}.</span>}</>}
        </span>
        <Button variant="secondary" onClick={onClose}>Close the round</Button>
        <Button busy={save.isPending} disabled={filled.length === 0 || bad.length > 0 || !measuredUtc} onClick={() => save.mutate()}>Save {filled.length || ""} reading{filled.length === 1 ? "" : "s"}</Button>
      </div>
    </section>
  );
}

function SettingsCard({ settings }: { settings: MonitoringSettings }) {
  const toast = useToast();
  const client = useQueryClient();
  const [days, setDays] = useState(String(settings.intervalDays));
  const [min, setMin] = useState(settings.minPressureBarg?.toString() ?? "");
  const save = useMutation({
    mutationFn: () => api<MonitoringSettings>("/api/settings/monitoring", { method: "PUT", body: { intervalDays: Number(days), minPressureBarg: min.trim() === "" ? null : Number(min.replace(",", ".")) } }),
    onSuccess: () => { client.invalidateQueries({ queryKey: ["monitoring"] }); toast("Monitoring settings saved"); },
    onError: (e) => toast(e.message, "error"),
  });
  return (
    <details className="mt-8 max-w-2xl rounded-lg border border-line bg-panel p-4">
      <summary className="cursor-pointer font-display text-lg font-semibold text-ink">Monitoring settings (all units)</summary>
      <form className="mt-3 flex flex-wrap items-end gap-3" onSubmit={(e) => { e.preventDefault(); save.mutate(); }}>
        <label>
          <span className="block text-sm font-semibold text-ink">Check pressure every (days)</span>
          <TextInput inputMode="numeric" value={days} onChange={(e) => setDays(e.target.value)} className="mt-1 w-32" />
        </label>
        <label>
          <span className="block text-sm font-semibold text-ink">Low pressure below (barg)</span>
          <TextInput inputMode="decimal" value={min} placeholder="Off" onChange={(e) => setMin(e.target.value)} className="mt-1 w-32" />
        </label>
        <Button type="submit" busy={save.isPending}>Save</Button>
      </form>
      <p className="mt-2 text-sm text-muted">
        Both apply only to assets marked as preserved, with their Pressure (barg) readings. Assets still being preserved (G4 N2 blanket) are never due or low.
        Leave the minimum empty until engineering confirms the value; nothing is flagged as low meanwhile.
      </p>
    </details>
  );
}
