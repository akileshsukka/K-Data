import { useEffect, useMemo, useRef, useState } from "react";
import { Link, useNavigate, useParams, useSearchParams } from "react-router";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api } from "../../api/client";
import { Perm, PipingFieldKeys, pipingFixedKeys, type AssetType, type DashboardCard, type RegisterRow } from "../../api/types";
import { useCustomFields, useLineCodes, useUnit } from "../../app/useCatalog";
import { useSession } from "../../app/session";
import { Button } from "../../components/Button";
import { SelectInput, TextInput } from "../../components/Field";
import { CurrentGateChip, GateStatus, gatePrefix, gateText } from "../../components/GateStatus";
import { PageHeader } from "../../components/PageHeader";
import { TagListFilter, tagKey, tagList } from "../../components/TagListFilter";
import { useDebounced } from "../../components/useDebounced";
import { formatDate } from "../../components/dates";
import { formatSize } from "../lines/lineNumber";
import { StatusTick, drainedTick, walkdownTick, yesTick } from "../lines/StatusTick";
import { PipingNotes } from "../lines/PipingNotes";
import { PipingSummaryCards } from "../lines/PipingSummaryCards";
import { CodeMeaning } from "../lines/CodeMeaning";
import { NonStandardBadge } from "../lines/FormatCheckToggle";
import { useToast } from "../../components/Toast";
import { AddToGroupDialog } from "../groups/AddToGroupDialog";
import { DownloadExcelButton } from "../excel/DownloadExcelButton";

const titles: Record<AssetType, string> = {
  Equipment: "Equipment Register",
  ElectricalInstrument: "E&I Register",
  Pipeline: "Pipeline Register",
};

interface Filters {
  text: string;
  discipline: string;
  currentGate: string;
  /** Gates to check (1–8). Several gates = all of them must match (AND). */
  gates: number[];
  gateStatus: string;
  actionBy: string;
  emWalkdown: string;
  ready: string;
  drained: string;
  inspection: string;
  cleaning: string;
  activity: string;
  /** Gate documentation: "pending" (some still pending) or "ready" (has documents, none pending). */
  docs: string;
  inactive: boolean;
  group: string;
  /** Internal control of lines (lines.nonStandard): "nonstandard" or "standard" line numbers. */
  numberCheck: string;
  /** Dashboard figure the register was opened from (tags, preserved, ready, hold, na, available, category-{id}). */
  card: string;
  /** Tags pasted from Excel (lower case, comma separated): only those rows are shown. */
  tags: string;
}

const empty: Filters = { text: "", discipline: "", currentGate: "", gates: [], gateStatus: "", actionBy: "", emWalkdown: "", ready: "", drained: "", inspection: "", cleaning: "", activity: "", numberCheck: "", docs: "", inactive: false, group: "", card: "", tags: "" };

/** Name of each filter in the page address (?status=Ongoing&gates=1,2…). */
const param: Record<keyof Filters, string> = {
  text: "q", discipline: "discipline", currentGate: "currentGate", gates: "gates", gateStatus: "status", actionBy: "actionBy",
  emWalkdown: "walkdown", ready: "ready", drained: "drained", inspection: "inspection", cleaning: "cleaning", activity: "activity",
  numberCheck: "number", docs: "docs", inactive: "inactive", group: "group", card: "card", tags: "tags",
};

/** Filters live in the page address, so Back (e.g. after opening an equipment) brings them back as they were. */
function readFilters(sp: URLSearchParams): Filters {
  const f: Filters = { ...empty, gates: [] };
  for (const key of Object.keys(param) as (keyof Filters)[]) {
    const v = sp.get(param[key]);
    if (v === null) continue;
    if (key === "gates") f.gates = v.split(",").map(Number).filter((g) => g >= 1 && g <= 8);
    else if (key === "inactive") f[key] = v === "1";
    // "docs=1" was the old "Documentation pending" checkbox: older links keep working.
    else if (key === "docs") f.docs = v === "1" ? "pending" : v === "pending" || v === "ready" ? v : "";
    else f[key] = v;
  }
  return f;
}

function writeFilters(f: Filters): URLSearchParams {
  const sp = new URLSearchParams();
  for (const key of Object.keys(param) as (keyof Filters)[]) {
    const v = f[key];
    if (Array.isArray(v)) { if (v.length) sp.set(param[key], v.join(",")); }
    else if (typeof v === "boolean") { if (v) sp.set(param[key], "1"); }
    else if (v) sp.set(param[key], v);
  }
  return sp;
}

/** Last filters of each register (per unit and type) for this browser tab, restored when the register is opened again from the menu. */
const memoryKey = (unitId: number, type: AssetType) => `kdata.register.${unitId}.${type}`;
const memory = {
  get: (key: string) => { try { return sessionStorage.getItem(key) ?? ""; } catch { return ""; } },
  set: (key: string, value: string) => { try { sessionStorage.setItem(key, value); } catch { /* storage unavailable: filters still live in the address */ } },
};

const otherRegister: Partial<Record<AssetType, { path: string; name: string }>> = {
  Equipment: { path: "ei", name: "E&I Register" },
  ElectricalInstrument: { path: "equipment", name: "Equipment Register" },
};

/** EM Walkdown Status values (Completed / Ongoing / N/A); "(not set)" finds the ones still blank. */
const walkdownStatuses = ["Completed", "Ongoing", "N/A"];

/** All assets of one type in the selected unit, with filters that apply as you type. */
export function RegisterPage({ type }: { type: AssetType }) {
  const unitId = Number(useParams().unitId);
  // A fresh page per unit, so the filters of one unit never carry over to another.
  return <RegisterView key={`${unitId}-${type}`} unitId={unitId} type={type} />;
}

function RegisterView({ unitId, type }: { unitId: number; type: AssetType }) {
  const { unit } = useUnit(unitId);
  const session = useSession();
  // Internal control: lines whose number is not standard (only roles with lines.nonStandard).
  const seesNumberCheck = type === "Pipeline" && session.can(Perm.linesNonStandard);
  const toast = useToast();
  const queryClient = useQueryClient();
  const reread = useMutation({
    mutationFn: () => api<{ lines: number; updated: number; stillNonStandard: number }>(`/api/units/${unitId}/lines/reread`, { method: "POST" }),
    onSuccess: (r) => {
      queryClient.invalidateQueries({ queryKey: ["register"] });
      toast(r.lines === 0 ? "No non-standard lines to read again." : `${r.lines} non-standard line${r.lines === 1 ? "" : "s"} read again: ${r.updated} got new values (empty fields only).`);
    },
    onError: (e) => toast(e.message, "error"),
  });
  const codes = useLineCodes();
  // Inspection Done / Cleaning Required / Cleaned of lines have their own place in the table.
  const customColumns = (useCustomFields(type).data ?? []).filter((c) => c.showInRegister && !(type === "Pipeline" && pipingFixedKeys.includes(c.key)));
  const navigate = useNavigate();
  // Filters are React state (every click applies at once) mirrored into the page address (Back and links keep them).
  const [searchParams, setSearchParams] = useSearchParams();
  const [f, setF] = useState<Filters>(() => readFilters(searchParams));
  const written = useRef(new Set<string>([searchParams.toString()]));
  useEffect(() => {
    const s = writeFilters(f).toString();
    if (s === searchParams.toString()) return;
    written.current.add(s);
    setSearchParams(s, { replace: true });
    // eslint-disable-next-line react-hooks/exhaustive-deps -- only when the filters change
  }, [f]);
  useEffect(() => {
    // The address changed by itself (Back/Forward, a dashboard link, restored filters): adopt it.
    const s = searchParams.toString();
    if (written.current.has(s)) return;
    written.current = new Set([s]);
    setF(readFilters(searchParams));
  }, [searchParams]);

  // Opened without filters in the address (menu, tile): bring back the last filters of this register, once.
  const restoredFor = useRef<string | null>(null);
  const key = memoryKey(unitId, type);
  useEffect(() => {
    if (restoredFor.current === key) return;
    restoredFor.current = key;
    const last = memory.get(key);
    if (searchParams.toString() === "" && last) setSearchParams(new URLSearchParams(last), { replace: true });
  }, [key, searchParams, setSearchParams]);
  useEffect(() => {
    if (restoredFor.current === key) memory.set(key, searchParams.toString());
  }, [key, searchParams]);
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [grouping, setGrouping] = useState(false);
  const text = useDebounced(f.text.trim().toLowerCase(), 300);

  const rows = useQuery({
    queryKey: ["register", unitId, type, f.inactive],
    queryFn: ({ signal }) => api<RegisterRow[]>(`/api/units/${unitId}/assets?type=${type}&includeInactive=${f.inactive}`, { signal }),
  });

  const all = rows.data ?? [];
  const card = useQuery({
    queryKey: ["dashboard-card", unitId, f.card],
    queryFn: ({ signal }) => api<DashboardCard>(`/api/units/${unitId}/dashboard/cards/${encodeURIComponent(f.card)}`, { signal }),
    enabled: !!f.card,
  });
  const cardIds = useMemo(() => (card.data ? new Set(card.data.assetIds) : null), [card.data]);
  const outsideCard = card.data ? card.data.assetIds.filter((id) => !all.some((r) => r.id === id)).length : 0;
  const distinct = (pick: (r: RegisterRow) => string | null) => [...new Set(all.map(pick).filter((v): v is string => !!v))].sort();
  const showLaterGates = all.some((r) => r.gates.slice(4).some(Boolean));
  // E&I items always show their 8 steps (E1–E8); equipment and lines show G5–G8 only once someone uses them.
  const gateCount = showLaterGates || type === "ElectricalInstrument" ? 8 : 4;
  const step = gatePrefix(type);

  const lines = type === "Pipeline";
  const tagSet = useMemo(() => new Set(tagList(f.tags)), [f.tags]);
  const tagsNotFound = [...tagSet].filter((t) => !all.some((r) => tagKey(r.tag ?? "") === t || tagKey(r.code) === t));
  const filtered = useMemo(
    () =>
      all.filter(
        (r) =>
          (!text || `${r.tag ?? ""} ${r.code} ${r.description ?? ""} ${r.preservationRemarks ?? ""} ${r.line?.pipeSpec ?? ""} ${r.line?.pidNumber ?? ""} ${r.line?.service ?? ""} ${r.circuit ?? ""} ${Object.values(r.custom ?? {}).join(" ")} ${codes.describe("Service", r.line?.service, unitId) ?? ""}`.toLowerCase().includes(text)) &&
          (!f.discipline || r.discipline === f.discipline) &&
          (!f.currentGate || r.currentGate === f.currentGate) &&
          gateMatch(r, f.gates, f.gateStatus, gateCount) &&
          (!f.actionBy || r.actionBy === f.actionBy) &&
          (!f.emWalkdown || (f.emWalkdown === "(not set)" ? !r.emWalkdown : r.emWalkdown?.toLowerCase() === f.emWalkdown.toLowerCase())) &&
          (!f.ready || (f.ready === "(not set)" ? !r.readyForPreservation : r.readyForPreservation?.toLowerCase() === f.ready.toLowerCase())) &&
          (!f.drained || (f.drained === "done") === (drainedTick(r.bulkLiquidDrained) === "done")) &&
          (!f.inspection || ({ Yes: "done", No: "missing", NA: "na" } as Record<string, string>)[f.inspection] === yesTick(r.custom?.[PipingFieldKeys.inspectionDone])) &&
          (!f.cleaning || (f.cleaning === "(not set)" ? !r.custom?.[PipingFieldKeys.cleaningRequired] : r.custom?.[PipingFieldKeys.cleaningRequired] === f.cleaning)) &&
          (!f.activity || (f.activity === "(none)" ? !r.currentActivity : (r.currentActivity ?? "").split("\n").includes(f.activity))) &&
          (!f.numberCheck || (f.numberCheck === "nonstandard") === !!r.line?.numberCheck?.nonStandard) &&
          (!f.docs || (f.docs === "pending" ? r.documentsPending > 0 : r.documentsPending === 0 && (r.documentsDone ?? 0) > 0)) &&
          (!f.card || !!cardIds?.has(r.id)) &&
          (tagSet.size === 0 || tagSet.has(tagKey(r.tag ?? "")) || tagSet.has(tagKey(r.code))) &&
          (!f.group || (f.group === "(none)" ? !r.groups?.length : !!r.groups?.includes(f.group))),
      ),
    [all, text, f, gateCount, codes, cardIds, tagSet],
  );

  const totalLength = lines ? filtered.reduce((sum, r) => sum + (r.line?.lengthM ?? 0), 0) : 0;
  const active = Object.entries(f).filter(([k, v]) => k !== "inactive" && v !== "" && v !== false && !(Array.isArray(v) && v.length === 0)).length;
  // Rows can be selected for bulk edit, groups and (E&I register) the E7 report of the selected motors.
  const canBulk = session.can(Perm.bulkEdit) || session.can(Perm.groupsManage) || (type === "ElectricalInstrument" && session.can(Perm.excelExport));
  const groupCodes = [...new Set(all.flatMap((r) => r.groups ?? []))].sort();
  const allSelected = filtered.length > 0 && filtered.every((r) => selected.has(r.id));

  function toggleAll() {
    const next = new Set(selected);
    if (allSelected) filtered.forEach((r) => next.delete(r.id));
    else filtered.forEach((r) => r.isActive && next.add(r.id));
    setSelected(next);
  }

  function toggle(id: string) {
    const next = new Set(selected);
    if (next.has(id)) next.delete(id);
    else next.add(id);
    setSelected(next);
  }

  const set = <K extends keyof Filters>(key: K, value: Filters[K]) => setF((prev) => ({ ...prev, [key]: value }));
  const toggleGate = (g: number) => setF((prev) => ({ ...prev, gates: prev.gates.includes(g) ? prev.gates.filter((x) => x !== g) : [...prev.gates, g].sort((a, b) => a - b) }));

  return (
    <div>
      <PageHeader
        title={titles[type]}
        description={unit ? `${unit.name} — ${filtered.length} of ${all.length} shown${lines && totalLength > 0 ? ` · ${totalLength.toLocaleString("en", { maximumFractionDigits: 1 })} m` : ""}` : undefined}
        actions={
          <>
            {session.can(Perm.groupsManage) && selected.size > 0 && (
              <Button variant="secondary" onClick={() => setGrouping(true)}>Add {selected.size} to group</Button>
            )}
            {session.can(Perm.bulkEdit) && selected.size > 0 && (
              <Button variant="secondary" onClick={() => navigate(`/u/${unitId}/bulk`, { state: { ids: [...selected] } })}>
                Bulk edit {selected.size} selected
              </Button>
            )}
            {session.can(Perm.excelExport) && type === "ElectricalInstrument" && selected.size > 0 && (
              <DownloadExcelButton unitId={unitId} variant="secondary" path={`/api/units/${unitId}/electrical-tests/export`} body={{ assetIds: [...selected] }}
                label={`E7 report (${selected.size} selected)`} />
            )}
            {session.can(Perm.excelExport) && type === "Equipment" && <DownloadExcelButton unitId={unitId} variant="secondary" />}
            {session.can(Perm.excelExport) && lines && <DownloadExcelButton unitId={unitId} variant="secondary" piping label="Download line list" />}
            {seesNumberCheck && session.can(Perm.assetsEdit) && all.some((r) => r.line?.numberCheck?.nonStandard) && (
              <Button variant="secondary" busy={reread.isPending} onClick={() => reread.mutate()} title="Reads the non-standard numbers again with the current pipe specs and line codes; fills only empty fields.">
                Re-read non-standard lines
              </Button>
            )}
            {session.can(Perm.assetsManage) && lines && (
              <Link to={`/u/${unitId}/lines/paste`} className="inline-flex min-h-10 items-center rounded-md border border-line bg-panel px-4 font-semibold text-ink hover:border-violet hover:text-violet">
                Add lines in bulk
              </Link>
            )}
            {session.can(Perm.assetsManage) && (
              <Link to={`/u/${unitId}/new?type=${type}`} className="inline-flex min-h-10 items-center rounded-md bg-violet px-4 font-semibold text-white hover:bg-violet-dark">
                New {type === "ElectricalInstrument" ? "E&I item" : lines ? "line" : "equipment"}
              </Link>
            )}
          </>
        }
      />

      {lines && <PipingSummaryCards unitId={unitId} />}
      {lines && <PipingNotes unitId={unitId} />}

      {/* With 8 gates (E&I, or G5–G8 in use) the gate buttons need about 21rem: that block gets two columns until the screen is wide enough. */}
      <div className={`mt-5 grid gap-2 sm:grid-cols-2 ${gateCount > 4
        ? "lg:grid-cols-3 2xl:grid-cols-[minmax(14rem,2fr)_repeat(2,minmax(8rem,1fr))_minmax(21rem,2fr)_minmax(8rem,1fr)]"
        : "lg:grid-cols-[minmax(14rem,2fr)_repeat(2,minmax(8rem,1fr))_minmax(19rem,1.6fr)_minmax(8rem,1fr)]"}`}>
        <TextInput type="search" placeholder={lines ? "Search line number, ID, spec, P&ID, service or remarks" : "Search tag, ID, description or remarks"} value={f.text} onChange={(e) => set("text", e.target.value)} aria-label="Search" className="mt-0" />
        <SelectInput value={f.discipline} onChange={(e) => set("discipline", e.target.value)} aria-label="Discipline" className="mt-0">
          <option value="">All disciplines</option>
          {distinct((r) => r.discipline).map((d) => <option key={d}>{d}</option>)}
        </SelectInput>
        <SelectInput value={f.currentGate} onChange={(e) => set("currentGate", e.target.value)} aria-label="Current Gate" className="mt-0">
          <option value="">Any Current Gate</option>
          {distinct((r) => r.currentGate).map((d) => <option key={d} value={d}>{gateText(d, type)}</option>)}
        </SelectInput>
        <div className={`flex min-w-0 flex-wrap gap-2 ${gateCount > 4 ? "sm:col-span-2 2xl:col-span-1" : ""}`}>
          <div role="group" aria-label="Gates (several = all must match)" title="Choose one or more gates. With several, every chosen gate must have the status." className="inline-flex shrink-0 overflow-hidden rounded-md border border-line">
            {Array.from({ length: gateCount }, (_, i) => i + 1).map((g) => (
              <button
                key={g}
                type="button"
                aria-pressed={f.gates.includes(g)}
                onClick={() => toggleGate(g)}
                className={`min-h-10 min-w-10 border-l border-line px-2 text-sm font-semibold first:border-l-0 ${f.gates.includes(g) ? "bg-violet text-white" : "bg-panel text-ink hover:text-violet"}`}
              >
                {step}{g}
              </button>
            ))}
          </div>
          <SelectInput value={f.gateStatus} onChange={(e) => set("gateStatus", e.target.value)} aria-label="Gate status" className="mt-0 min-w-[8rem] flex-1">
            <option value="">Any status</option>
            {["Completed", "Ongoing", "Hold", "Not Applicable", "(empty)"].map((s) => <option key={s}>{s}</option>)}
          </SelectInput>
        </div>
        <SelectInput value={f.actionBy} onChange={(e) => set("actionBy", e.target.value)} aria-label="Action By" className="mt-0">
          <option value="">Any Action By</option>
          {distinct((r) => r.actionBy).map((d) => <option key={d}>{d}</option>)}
        </SelectInput>
      </div>
      <div className="mt-2 flex flex-wrap items-center gap-x-5 gap-y-2 text-[15px]">
        {groupCodes.length > 0 && (
          <div className="w-full sm:w-52">
            <SelectInput value={f.group} onChange={(e) => set("group", e.target.value)} aria-label="Workpack or group" className="mt-0">
              <option value="">Any workpack</option>
              {groupCodes.map((c) => <option key={c}>{c}</option>)}
              <option value="(none)">Not in a group</option>
            </SelectInput>
          </div>
        )}
        <div className="w-full sm:w-52">
            <SelectInput value={f.emWalkdown} onChange={(e) => set("emWalkdown", e.target.value)} aria-label="EM Walkdown" className="mt-0">
              <option value="">Any EM Walkdown</option>
              {[...walkdownStatuses, ...distinct((r) => r.emWalkdown).filter((v) => !walkdownStatuses.some((s) => s.toLowerCase() === v.toLowerCase()))].map((s) => <option key={s} value={s}>EM Walkdown: {s}</option>)}
              <option value="(not set)">EM Walkdown: not set</option>
            </SelectInput>
          </div>
        {!lines && (
          <div className="w-full sm:w-60">
            <SelectInput value={f.ready} onChange={(e) => set("ready", e.target.value)} aria-label="Ready For Preservation" className="mt-0">
              <option value="">Any Ready for preservation</option>
              <option value="Yes">Ready for preservation: Yes</option>
              <option value="No">Ready for preservation: No</option>
              <option value="(not set)">Ready for preservation: not set</option>
            </SelectInput>
          </div>
        )}
        {lines && (
          <>
            <div className="w-full sm:w-48">
              <SelectInput value={f.drained} onChange={(e) => set("drained", e.target.value)} aria-label="Bulk liquid drained" className="mt-0">
                <option value="">Any drained</option>
                <option value="done">Drained</option>
                <option value="not">Not drained</option>
              </SelectInput>
            </div>
            <div className="w-full sm:w-48">
              <SelectInput value={f.inspection} onChange={(e) => set("inspection", e.target.value)} aria-label="Inspection Done" className="mt-0">
                <option value="">Any inspection</option>
                <option value="Yes">Inspection done</option>
                <option value="No">Inspection not done</option>
                <option value="NA">Inspection N/A</option>
              </SelectInput>
            </div>
            <div className="w-full sm:w-52">
              <SelectInput value={f.cleaning} onChange={(e) => set("cleaning", e.target.value)} aria-label="Cleaning Required" className="mt-0">
                <option value="">Any cleaning</option>
                {distinct((r) => r.custom?.[PipingFieldKeys.cleaningRequired] ?? null).map((v) => <option key={v} value={v}>Cleaning: {v}</option>)}
                <option value="(not set)">Cleaning: not set</option>
              </SelectInput>
            </div>
            <div className="w-full sm:w-60">
              <SelectInput value={f.activity} onChange={(e) => set("activity", e.target.value)} aria-label="Current Activity" className="mt-0">
                <option value="">Any activity</option>
                {[...new Set(all.flatMap((r) => (r.currentActivity ?? "").split("\n").filter(Boolean)))].sort().map((v) => <option key={v} value={v}>{v}</option>)}
                <option value="(none)">No activity</option>
              </SelectInput>
            </div>
            {seesNumberCheck && (
              <div className="w-full sm:w-60">
                <SelectInput value={f.numberCheck} onChange={(e) => set("numberCheck", e.target.value)} aria-label="Line number format" className="mt-0">
                  <option value="">Any line number</option>
                  <option value="nonstandard">Non-standard line number</option>
                  <option value="standard">Standard line number</option>
                </SelectInput>
              </div>
            )}
          </>
        )}
        <TagListFilter value={f.tags} onChange={(v) => set("tags", v)} />
        <div className="w-full sm:w-60">
          <SelectInput value={f.docs} onChange={(e) => set("docs", e.target.value)} aria-label="Documentation" className="mt-0"
            title="Gate documentation. Ready: has gate documents and none is pending (received or N/A).">
            <option value="">Any documentation</option>
            <option value="pending">Documentation pending</option>
            <option value="ready">Documentation ready</option>
          </SelectInput>
        </div>
        <label className="inline-flex items-center gap-2">
          <input type="checkbox" className="size-4 accent-violet" checked={f.inactive} onChange={(e) => set("inactive", e.target.checked)} />
          Show inactive
        </label>
      </div>

      {active > 0 && (
        <div className="mt-3 flex flex-wrap items-center justify-between gap-3 rounded-lg border border-violet/30 bg-violet-soft px-4 py-3" role="status">
          <div className="min-w-0">
            <p className="font-semibold text-ink">
              {active === 1 ? "1 filter applied" : `${active} filters applied`} · showing {filtered.length} of {all.length}
            </p>
            {tagSet.size > 0 && (
              <p className="mt-0.5 text-sm text-muted">
                Pasted tags: {tagSet.size - tagsNotFound.length} of {tagSet.size} found
                {tagsNotFound.length > 0 && <> · <span className="text-hold">not found: {tagsNotFound.slice(0, 15).join(", ").toUpperCase()}{tagsNotFound.length > 15 ? ` and ${tagsNotFound.length - 15} more` : ""}</span></>}
                {" · "}<button type="button" className="font-semibold text-violet" onClick={() => set("tags", "")}>Remove this filter</button>
              </p>
            )}
            {f.card && (
              <p className="mt-0.5 text-sm text-muted">
                From the dashboard: <span className="font-semibold text-ink">{card.data?.label ?? (card.isError ? "figure not found" : "loading…")}</span>
                {card.data && ` (${card.data.count})`}
                {" · "}<Link to={`/u/${unitId}`} className="font-semibold text-violet">Back to dashboard</Link>
                {" · "}<button type="button" className="font-semibold text-violet" onClick={() => set("card", "")}>Remove this filter</button>
              </p>
            )}
            {outsideCard > 0 && otherRegister[type] && (
              <p className="mt-0.5 text-sm text-muted">
                {outsideCard} more of this figure are in the{" "}
                <Link to={`/u/${unitId}/${otherRegister[type]!.path}?card=${encodeURIComponent(f.card)}`} className="font-semibold text-violet">{otherRegister[type]!.name}</Link>.
              </p>
            )}
          </div>
          <Button onClick={() => setF({ ...empty, inactive: f.inactive })} className="min-h-11 px-6 text-base">
            ✕ Clear filters
          </Button>
        </div>
      )}

      <AddToGroupDialog open={grouping} onClose={() => setGrouping(false)} unitId={unitId} assetIds={[...selected]} onDone={() => setSelected(new Set())} />

      {(rows.isPending || (f.card && card.isPending)) && <p className="mt-6 text-muted">Loading…</p>}
      {card.isError && <p className="mt-6 text-hold">This dashboard figure no longer exists (the category may have been removed). Clear the filters to see everything.</p>}
      {rows.error && <p className="mt-6 text-hold">{rows.error.message}</p>}
      {rows.data && !(f.card && card.isPending) && filtered.length === 0 && <p className="mt-6 text-muted">{all.length === 0 ? (lines ? "No lines in this unit yet. Add one, or paste the line list." : "No assets of this type in the unit yet.") : "Nothing matches these filters."}</p>}

      {filtered.length > 0 && (
        <>
          <div className="mt-4 hidden overflow-x-auto rounded-lg border border-line bg-panel md:block">
            <table className="w-full border-separate border-spacing-0 text-left text-[15px]">
              <thead className="sticky top-0 bg-panel font-display text-muted">
                <tr>
                  {canBulk && (
                    <th className="w-10 border-b border-line px-3 py-2.5">
                      <input type="checkbox" className="size-4 accent-violet" checked={allSelected} onChange={toggleAll} aria-label="Select all shown" />
                    </th>
                  )}
                  <th className="border-b border-line px-3 py-2.5 font-semibold">{lines ? "Line number" : "Tag"}</th>
                  {lines ? (
                    <>
                      <th className="border-b border-line px-3 py-2.5 font-semibold">Service</th>
                      <th className="border-b border-line px-3 py-2.5 font-semibold">P&ID</th>
                      <th className="border-b border-line px-3 py-2.5 font-semibold">Circuit</th>
                      <th className="border-b border-line px-2 py-2.5 text-center font-semibold" title="EM Walkdown · Bulk liquid drained · Inspection Done">
                        <span className="inline-flex gap-1.5"><abbr title="EM Walkdown" className="w-5 no-underline">W</abbr><abbr title="Bulk liquid drained" className="w-5 no-underline">D</abbr><abbr title="Inspection Done" className="w-5 no-underline">I</abbr></span>
                      </th>
                      <th className="border-b border-line px-3 py-2.5 font-semibold">Current Activity</th>
                      <th className="border-b border-line px-3 py-2.5 font-semibold">Cleaning Required</th>
                    </>
                  ) : (
                    <>
                      <th className="border-b border-line px-3 py-2.5 font-semibold">Description</th>
                      <th className="border-b border-line px-3 py-2.5 font-semibold">Discipline</th>
                    </>
                  )}
                  {customColumns.map((c) => <th key={c.key} className="border-b border-line px-3 py-2.5 font-semibold">{c.label}</th>)}
                  <th className="border-b border-line px-3 py-2.5 font-semibold">Current Gate</th>
                  {Array.from({ length: gateCount }, (_, i) => (
                    <th key={i} className="border-b border-line px-2 py-2.5 font-semibold">{step}{i + 1}</th>
                  ))}
                  <th className="border-b border-line px-3 py-2.5 font-semibold">Action By</th>
                </tr>
              </thead>
              <tbody>
                {filtered.map((r) => (
                  <tr key={r.id} className={`group ${r.isActive ? "" : "text-muted"} hover:bg-violet-soft/50`}>
                    {canBulk && (
                      <td className="border-b border-line px-3 py-2">
                        <input type="checkbox" className="size-4 accent-violet" checked={selected.has(r.id)} disabled={!r.isActive} onChange={() => toggle(r.id)} aria-label={`Select ${r.tag ?? r.code}`} />
                      </td>
                    )}
                    <td className="border-b border-line px-3 py-2">
                      <Link to={`/assets/${r.id}`} className="font-semibold whitespace-pre text-ink group-hover:text-violet">{r.tag ?? r.code}</Link>
                      <span className="block text-sm whitespace-nowrap text-muted">{r.code}{!r.isActive && " · inactive"}</span>
                      {r.line?.numberCheck?.nonStandard && <NonStandardBadge note={r.line.numberCheck.note} className="mt-1" />}
                    </td>
                    {lines ? (
                      <>
                        <td className="max-w-[14rem] border-b border-line px-3 py-2"><CodeMeaning code={r.line?.service} meaning={codes.describe("Service", r.line?.service, unitId)} /></td>
                        <td className="border-b border-line px-3 py-2 text-sm whitespace-pre">{r.line?.pidNumber}</td>
                        <td className="border-b border-line px-3 py-2 text-sm whitespace-nowrap">{r.circuit}</td>
                        <td className="border-b border-line px-2 py-2 text-center">
                          <span className="inline-flex gap-1.5">
                            <StatusTick tick={walkdownTick(r.emWalkdown)} label="EM Walkdown" value={r.emWalkdown} />
                            <StatusTick tick={drainedTick(r.bulkLiquidDrained)} label="Bulk liquid drained" value={r.bulkLiquidDrained} />
                            <StatusTick tick={yesTick(r.custom?.[PipingFieldKeys.inspectionDone])} label="Inspection Done" value={r.custom?.[PipingFieldKeys.inspectionDone] ?? "No"} />
                          </span>
                        </td>
                        <td className="max-w-[16rem] border-b border-line px-3 py-2 text-sm whitespace-pre-line">{r.currentActivity}</td>
                        <td className="border-b border-line px-3 py-2 text-sm whitespace-nowrap">{r.custom?.[PipingFieldKeys.cleaningRequired] ?? ""}</td>
                      </>
                    ) : (
                      <>
                        <td className="max-w-[22rem] border-b border-line px-3 py-2">
                          <span className="line-clamp-2">{r.description}</span>
                        </td>
                        <td className="border-b border-line px-3 py-2 whitespace-nowrap">{r.discipline}</td>
                      </>
                    )}
                    {customColumns.map((c) => <td key={c.key} className="border-b border-line px-3 py-2 text-sm">{c.dataType === "Date" && r.custom?.[c.key] ? <span className="whitespace-nowrap">{formatDate(r.custom[c.key])}</span> : r.custom?.[c.key] ?? ""}</td>)}
                    <td className="border-b border-line px-3 py-2">
                      <CurrentGateChip value={r.currentGate} type={type} />
                      {r.documentsPending > 0 && <span className="mt-1 block text-sm text-ongoing">{r.documentsPending} doc. pending</span>}
                      {r.documentsPending === 0 && (r.documentsDone ?? 0) > 0 && <span className="mt-1 block text-sm text-done">Docs ready</span>}
                    </td>
                    {r.gates.slice(0, gateCount).map((g, i) => (
                      <td key={i} className="border-b border-line px-2 py-2"><GateStatus value={g} compact /></td>
                    ))}
                    <td className="border-b border-line px-3 py-2 text-sm">{r.actionBy ?? ""}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>

          <ul className="mt-4 space-y-2 md:hidden">
            {filtered.map((r) => (
              <li key={r.id}>
                <Link to={`/assets/${r.id}`} className={`block rounded-lg border border-line bg-panel p-3 ${r.isActive ? "" : "opacity-70"}`}>
                  <div className="flex items-start justify-between gap-3">
                    <div className="min-w-0">
                      <p className="font-semibold break-words whitespace-pre-line text-ink">{r.tag ?? r.code}</p>
                      {r.line?.numberCheck?.nonStandard && <NonStandardBadge note={r.line.numberCheck.note} />}
                      <p className="truncate text-sm text-muted">{lines ? [formatSize(r.line?.sizeIn), codes.describe("Service", r.line?.service, unitId) ?? r.line?.service, r.circuit, r.custom?.[PipingFieldKeys.cleaningRequired] && `Cleaning: ${r.custom[PipingFieldKeys.cleaningRequired]}`].filter(Boolean).join(" · ") : r.description}</p>
                      {lines && (
                        <span className="mt-1 inline-flex items-center gap-1.5 text-sm text-muted">
                          <StatusTick tick={walkdownTick(r.emWalkdown)} label="EM Walkdown" value={r.emWalkdown} />
                          <StatusTick tick={drainedTick(r.bulkLiquidDrained)} label="Bulk liquid drained" value={r.bulkLiquidDrained} />
                          <StatusTick tick={yesTick(r.custom?.[PipingFieldKeys.inspectionDone])} label="Inspection Done" value={r.custom?.[PipingFieldKeys.inspectionDone] ?? "No"} />
                          {r.currentActivity && <span className="ml-1 truncate">{r.currentActivity.split("\n").join(", ")}</span>}
                        </span>
                      )}
                    </div>
                    <CurrentGateChip value={r.currentGate} type={type} />
                  </div>
                  <div className="mt-2 flex flex-wrap gap-1">
                    {r.gates.slice(0, gateCount).map((g, i) => (
                      <span key={i} className="inline-flex items-center gap-1 text-sm text-muted">{step}{i + 1}<GateStatus value={g} compact /></span>
                    ))}
                  </div>
                </Link>
              </li>
            ))}
          </ul>
        </>
      )}
    </div>
  );
}

/**
 * Gate filter.
 * - Gates chosen: EVERY chosen gate must match (G1 + G2 + Ongoing = both G1 and G2 Ongoing). Without a status, every chosen gate must be filled in.
 * - No gate chosen, status chosen: any of the shown gates has that status.
 */
export function gateMatch(r: RegisterRow, gates: number[], status: string, gateCount: number) {
  const chosen = gates.filter((g) => g <= gateCount);
  if (chosen.length === 0 && !status) return true;
  const test = (v: string | null | undefined) => (!status ? !!v : status === "(empty)" ? !v : v === status);
  if (chosen.length === 0) return r.gates.slice(0, gateCount).some(test);
  return chosen.every((g) => test(r.gates[g - 1]));
}
