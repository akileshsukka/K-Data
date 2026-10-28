import { useMemo, useState } from "react";
import { Link, useNavigate, useParams } from "react-router";
import { useQuery } from "@tanstack/react-query";
import { api } from "../../api/client";
import { Perm, type AssetType, type RegisterRow } from "../../api/types";
import { useCustomFields, useLineCodes, useUnit } from "../../app/useCatalog";
import { useSession } from "../../app/session";
import { Button } from "../../components/Button";
import { SelectInput, TextInput } from "../../components/Field";
import { CurrentGateChip, GateStatus } from "../../components/GateStatus";
import { PageHeader } from "../../components/PageHeader";
import { useDebounced } from "../../components/useDebounced";
import { formatDate } from "../../components/dates";
import { formatSize } from "../lines/lineNumber";
import { CodeMeaning } from "../lines/CodeMeaning";
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
  gate: string;
  gateStatus: string;
  actionBy: string;
  emWalkdown: string;
  docsPending: boolean;
  inactive: boolean;
  group: string;
}

const empty: Filters = { text: "", discipline: "", currentGate: "", gate: "", gateStatus: "", actionBy: "", emWalkdown: "", docsPending: false, inactive: false, group: "" };

/** EM Walkdown Status values (Completed / Ongoing / N/A); "(not set)" finds the ones still blank. */
const walkdownStatuses = ["Completed", "Ongoing", "N/A"];

/** All assets of one type in the selected unit, with filters that apply as you type. */
export function RegisterPage({ type }: { type: AssetType }) {
  const unitId = Number(useParams().unitId);
  const { unit } = useUnit(unitId);
  const session = useSession();
  const codes = useLineCodes();
  const customColumns = (useCustomFields(type).data ?? []).filter((c) => c.showInRegister);
  const navigate = useNavigate();
  const [f, setF] = useState<Filters>(empty);
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [grouping, setGrouping] = useState(false);
  const text = useDebounced(f.text.trim().toLowerCase(), 300);

  const rows = useQuery({
    queryKey: ["register", unitId, type, f.inactive],
    queryFn: ({ signal }) => api<RegisterRow[]>(`/api/units/${unitId}/assets?type=${type}&includeInactive=${f.inactive}`, { signal }),
  });

  const all = rows.data ?? [];
  const distinct = (pick: (r: RegisterRow) => string | null) => [...new Set(all.map(pick).filter((v): v is string => !!v))].sort();
  const showLaterGates = all.some((r) => r.gates.slice(4).some(Boolean));
  const gateCount = showLaterGates ? 8 : 4;

  const lines = type === "Pipeline";
  const filtered = useMemo(
    () =>
      all.filter(
        (r) =>
          (!text || `${r.tag ?? ""} ${r.code} ${r.description ?? ""} ${r.preservationRemarks ?? ""} ${r.line?.pipeSpec ?? ""} ${r.line?.pidNumber ?? ""} ${r.line?.service ?? ""} ${r.circuit ?? ""} ${Object.values(r.custom ?? {}).join(" ")} ${codes.describe("Service", r.line?.service) ?? ""}`.toLowerCase().includes(text)) &&
          (!f.discipline || r.discipline === f.discipline) &&
          (!f.currentGate || r.currentGate === f.currentGate) &&
          gateMatch(r, f.gate, f.gateStatus, gateCount) &&
          (!f.actionBy || r.actionBy === f.actionBy) &&
          (!f.emWalkdown || (f.emWalkdown === "(not set)" ? !r.emWalkdown : r.emWalkdown?.toLowerCase() === f.emWalkdown.toLowerCase())) &&
          (!f.docsPending || r.documentsPending > 0) &&
          (!f.group || (f.group === "(none)" ? !r.groups?.length : !!r.groups?.includes(f.group))),
      ),
    [all, text, f, gateCount, codes],
  );

  const totalLength = lines ? filtered.reduce((sum, r) => sum + (r.line?.lengthM ?? 0), 0) : 0;
  const active = Object.entries(f).filter(([k, v]) => k !== "inactive" && v !== "" && v !== false).length;
  const canBulk = session.can(Perm.bulkEdit) || session.can(Perm.groupsManage);
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
            {session.can(Perm.excelExport) && type === "Equipment" && <DownloadExcelButton unitId={unitId} variant="secondary" />}
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

      <div className="mt-5 grid gap-2 sm:grid-cols-2 lg:grid-cols-[minmax(14rem,2fr)_repeat(2,minmax(8rem,1fr))_minmax(17rem,1.5fr)_minmax(8rem,1fr)]">
        <TextInput type="search" placeholder={lines ? "Search line number, ID, spec, P&ID, service or remarks" : "Search tag, ID, description or remarks"} value={f.text} onChange={(e) => set("text", e.target.value)} aria-label="Search" className="mt-0" />
        <SelectInput value={f.discipline} onChange={(e) => set("discipline", e.target.value)} aria-label="Discipline" className="mt-0">
          <option value="">All disciplines</option>
          {distinct((r) => r.discipline).map((d) => <option key={d}>{d}</option>)}
        </SelectInput>
        <SelectInput value={f.currentGate} onChange={(e) => set("currentGate", e.target.value)} aria-label="Current Gate" className="mt-0">
          <option value="">Any Current Gate</option>
          {distinct((r) => r.currentGate).map((d) => <option key={d}>{d}</option>)}
        </SelectInput>
        <div className="grid grid-cols-[7.5rem_minmax(0,1fr)] gap-2">
          <SelectInput value={f.gate} onChange={(e) => set("gate", e.target.value)} aria-label="Gate" className="mt-0">
            <option value="">Any gate</option>
            {Array.from({ length: 8 }, (_, i) => <option key={i} value={i + 1}>G{i + 1}</option>)}
          </SelectInput>
          <SelectInput value={f.gateStatus} onChange={(e) => set("gateStatus", e.target.value)} aria-label="Gate status" className="mt-0">
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
          <div className="w-52">
            <SelectInput value={f.group} onChange={(e) => set("group", e.target.value)} aria-label="Workpack or group" className="mt-0">
              <option value="">Any workpack</option>
              {groupCodes.map((c) => <option key={c}>{c}</option>)}
              <option value="(none)">Not in a group</option>
            </SelectInput>
          </div>
        )}
        {!lines && (
          <div className="w-52">
            <SelectInput value={f.emWalkdown} onChange={(e) => set("emWalkdown", e.target.value)} aria-label="EM Walkdown" className="mt-0">
              <option value="">Any EM Walkdown</option>
              {[...walkdownStatuses, ...distinct((r) => r.emWalkdown).filter((v) => !walkdownStatuses.some((s) => s.toLowerCase() === v.toLowerCase()))].map((s) => <option key={s} value={s}>EM Walkdown: {s}</option>)}
              <option value="(not set)">EM Walkdown: not set</option>
            </SelectInput>
          </div>
        )}
        <label className="inline-flex items-center gap-2">
          <input type="checkbox" className="size-4 accent-violet" checked={f.docsPending} onChange={(e) => set("docsPending", e.target.checked)} />
          Documentation pending
        </label>
        <label className="inline-flex items-center gap-2">
          <input type="checkbox" className="size-4 accent-violet" checked={f.inactive} onChange={(e) => set("inactive", e.target.checked)} />
          Show inactive
        </label>
        {active > 0 && <button className="font-semibold text-violet" onClick={() => setF({ ...empty, inactive: f.inactive })}>Clear filters</button>}
      </div>

      <AddToGroupDialog open={grouping} onClose={() => setGrouping(false)} unitId={unitId} assetIds={[...selected]} onDone={() => setSelected(new Set())} />

      {rows.isPending && <p className="mt-6 text-muted">Loading…</p>}
      {rows.error && <p className="mt-6 text-hold">{rows.error.message}</p>}
      {rows.data && filtered.length === 0 && <p className="mt-6 text-muted">{all.length === 0 ? (lines ? "No lines in this unit yet. Add one, or paste the line list." : "No assets of this type in the unit yet.") : "Nothing matches these filters."}</p>}

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
                      <th className="border-b border-line px-3 py-2.5 text-right font-semibold">Size</th>
                      <th className="border-b border-line px-3 py-2.5 font-semibold">Spec</th>
                      <th className="border-b border-line px-3 py-2.5 font-semibold">Rating</th>
                      <th className="border-b border-line px-3 py-2.5 font-semibold">Face</th>
                      <th className="border-b border-line px-3 py-2.5 font-semibold">Gasket</th>
                      <th className="border-b border-line px-3 py-2.5 font-semibold">Service</th>
                      <th className="border-b border-line px-3 py-2.5 font-semibold">P&ID</th>
                      <th className="border-b border-line px-3 py-2.5 font-semibold">Circuit</th>
                      <th className="border-b border-line px-3 py-2.5 text-right font-semibold">Length (m)</th>
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
                    <th key={i} className="border-b border-line px-2 py-2.5 font-semibold">G{i + 1}</th>
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
                      <Link to={`/assets/${r.id}`} className="font-semibold whitespace-nowrap text-ink group-hover:text-violet">{r.tag ?? r.code}</Link>
                      <span className="block text-sm whitespace-nowrap text-muted">{r.code}{!r.isActive && " · inactive"}</span>
                    </td>
                    {lines ? (
                      <>
                        <td className="border-b border-line px-3 py-2 text-right tabular-nums whitespace-nowrap">{formatSize(r.line?.sizeIn)}</td>
                        <td className="border-b border-line px-3 py-2 whitespace-nowrap">{r.line?.pipeSpec}</td>
                        <td className="border-b border-line px-3 py-2 whitespace-nowrap">{r.line?.pressureClass ?? <SpecMissing spec={r.line?.pipeSpec} />}</td>
                        <td className="border-b border-line px-3 py-2 whitespace-nowrap">{r.line?.flangeType}</td>
                        <td className="border-b border-line px-3 py-2 text-sm">{r.line?.gasket}</td>
                        <td className="max-w-[14rem] border-b border-line px-3 py-2"><CodeMeaning code={r.line?.service} meaning={codes.describe("Service", r.line?.service)} /></td>
                        <td className="border-b border-line px-3 py-2 text-sm whitespace-nowrap">{r.line?.pidNumber}</td>
                        <td className="border-b border-line px-3 py-2 text-sm whitespace-nowrap">{r.circuit}</td>
                        <td className="border-b border-line px-3 py-2 text-right tabular-nums">{r.line?.lengthM ?? ""}</td>
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
                      <CurrentGateChip value={r.currentGate} />
                      {r.documentsPending > 0 && <span className="mt-1 block text-sm text-ongoing">{r.documentsPending} doc. pending</span>}
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
                      <p className="truncate font-semibold text-ink">{r.tag ?? r.code}</p>
                      <p className="truncate text-sm text-muted">{lines ? [formatSize(r.line?.sizeIn), r.line?.pipeSpec, [r.line?.pressureClass, r.line?.flangeType].filter(Boolean).join(" "), codes.describe("Service", r.line?.service) ?? r.line?.service, r.line?.lengthM != null ? `${r.line.lengthM} m` : null].filter(Boolean).join(" · ") : r.description}</p>
                    </div>
                    <CurrentGateChip value={r.currentGate} />
                  </div>
                  <div className="mt-2 flex flex-wrap gap-1">
                    {r.gates.slice(0, gateCount).map((g, i) => (
                      <span key={i} className="inline-flex items-center gap-1 text-sm text-muted">G{i + 1}<GateStatus value={g} compact /></span>
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

/** Gate filter: a gate and a status, either one alone (any gate with that status / that gate filled in), or both. */
function gateMatch(r: RegisterRow, gate: string, status: string, gateCount: number) {
  if (!gate && !status) return true;
  const values = gate ? [r.gates[Number(gate) - 1]] : r.gates.slice(0, gateCount);
  if (!status) return values.some(Boolean);
  return status === "(empty)" ? values.some((v) => !v) : values.some((v) => v === status);
}

/** A line whose spec is not in the pipe spec list has no rating, face or gasket yet. */
function SpecMissing({ spec }: { spec: string | null | undefined }) {
  if (!spec) return null;
  return <span className="text-sm text-ongoing" title={`Add ${spec} to Admin → Pipe specs to get its rating, face and gasket`}>Spec not listed</span>;
}
