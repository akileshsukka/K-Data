import { useEffect, useState } from "react";
import { Link, useParams, useSearchParams } from "react-router";
import { useQuery } from "@tanstack/react-query";
import { api } from "../../api/client";
import { Perm, type ActivityRow, type CategoryRow, type Dashboard, type DashboardScope } from "../../api/types";
import { lastUnit, useUnit } from "../../app/useCatalog";
import { useSession } from "../../app/session";
import { PageHeader } from "../../components/PageHeader";
import { DownloadExcelButton } from "../excel/DownloadExcelButton";
import { PipingSummaryCards } from "../lines/PipingSummaryCards";
import { ExportPdfButton } from "./UnitDashboardPdf";

/** The two dashboards of a unit, as the switch at the top of the overview names them. */
export const scopeNames: Record<DashboardScope, { tab: string; title: string; register: string; path: string }> = {
  mechanical: { tab: "Mechanical equipment", title: "Mechanical equipment (static and rotating)", register: "Equipment Register", path: "equipment" },
  ei: { tab: "Electrical & Instrumentation", title: "Electrical & Instrumentation", register: "E&I Register", path: "ei" },
};

/** Dashboard of one scope, shared by the overview and the PDF. */
export const dashboardUrl = (unitId: number, scope: DashboardScope) => `/api/units/${unitId}/dashboard${scope === "ei" ? "?scope=ei" : ""}`;

/**
 * Unit overview = the client dashboards, computed by the API exactly like the dashboard sheets of the unit Excel, so
 * both always show the same numbers: "Mechanical equipment" (sheet Dashboard: static and rotating, E&I excluded) and
 * "Electrical & Instrumentation" (sheet E&I Dashboard). The switch is kept in the address (?view=ei).
 */
export function UnitHomePage() {
  const unitId = Number(useParams().unitId);
  const { unit, plantName, isPending } = useUnit(unitId);
  const session = useSession();
  const [params, setParams] = useSearchParams();
  const scope: DashboardScope = params.get("view") === "ei" ? "ei" : "mechanical";
  const dashboard = useQuery({
    queryKey: ["dashboard", unitId, scope],
    queryFn: ({ signal }) => api<Dashboard>(dashboardUrl(unitId, scope), { signal }),
    enabled: !!unit,
  });

  useEffect(() => {
    if (unit) lastUnit.set(unit.id);
  }, [unit]);

  if (isPending) return <p className="text-muted">Loading unit…</p>;
  if (!unit) {
    return (
      <div>
        <PageHeader title="Unit not found" description="The unit does not exist or was deactivated." />
        <Link to="/units" className="mt-6 inline-block font-semibold text-violet">Choose another unit</Link>
      </div>
    );
  }

  const d = dashboard.data?.scope === scope ? dashboard.data : undefined;
  const ei = scope === "ei";
  const setScope = (s: DashboardScope) =>
    setParams((p) => {
      if (s === "ei") p.set("view", "ei");
      else p.delete("view");
      return p;
    }, { replace: true });

  return (
    <div>
      <PageHeader
        title={unit.name}
        description={`${plantName} · preservation dashboard`}
        actions={
          <>
            <ExportPdfButton unitId={unit.id} unitName={unit.name} plantName={plantName ?? ""} />
            {session.can(Perm.excelExport) && <DownloadExcelButton unitId={unit.id} variant="secondary" />}
            {session.can(Perm.assetsManage) && (
              <Link to={`/u/${unit.id}/new`} className="inline-flex min-h-10 items-center rounded-md bg-violet px-4 font-semibold text-white hover:bg-violet-dark">New equipment</Link>
            )}
          </>
        }
      />

      <div className="mt-5 flex w-fit max-w-full flex-wrap rounded-md border border-line bg-panel p-1" role="tablist" aria-label="Dashboard">
        {(["mechanical", "ei"] as const).map((s) => (
          <button key={s} type="button" role="tab" aria-selected={scope === s} onClick={() => setScope(s)}
            className={`min-h-9 rounded px-4 font-semibold ${scope === s ? "bg-violet text-white" : "text-ink hover:text-violet"}`}>
            {scopeNames[s].tab}
          </button>
        ))}
      </div>

      {dashboard.isPending && <p className="mt-6 text-muted">Loading dashboard…</p>}
      {dashboard.isError && <p className="mt-6 text-hold">{dashboard.error.message}</p>}

      {d && d.totalTags === 0 && (
        <div className="mt-6 max-w-xl rounded-lg border border-dashed border-line bg-panel p-6">
          <h2 className="text-xl font-semibold">{ei ? "No E&I items in this unit yet" : "No equipment in this unit yet"}</h2>
          <p className="mt-2 text-muted">
            {ei
              ? "E&I items arrive with an Onboarding list on the Excel page (E&I list), or “New equipment” with an Electrical or Instrumentation discipline."
              : "Equipment arrives with the migration, an Onboarding list on the Excel page, or “New equipment”."}
          </p>
        </div>
      )}

      {d && d.totalTags > 0 && <ScopeDashboard d={d} unitId={unit.id} canEdit={session.can(Perm.categoriesManage)} />}

      {d && !ei && d.lineCount > 0 && <PipingSummaryCards unitId={unit.id} title="Pipelines" />}

      {d && (
        <section className="mt-6 grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
          <Tile to={`/u/${unit.id}/equipment`} title="Equipment Register" detail={d.scope === "mechanical" ? `${Object.values(d.byCurrentGate).reduce((a, b) => a + b, 0)} mechanical equipment` : "Static, rotating and package equipment"} />
          <Tile to={`/u/${unit.id}/lines`} title="Pipeline Register" detail={d.lineCount === 0 ? "No lines yet" : `${d.lineCount} lines · ${Math.round(d.lineLengthM).toLocaleString("en")} m · ${d.linesBlanketed} N2 blanketed`} />
          <Tile to={`/u/${unit.id}/ei`} title="E&I Register" detail={`${d.electricalInstrumentCount} electrical and instrumentation items`} />
          <Tile to={`/u/${unit.id}/monitoring`} title="Monitoring" detail="N2 blanket checks during and after preservation" />
          {session.can(Perm.groupsView) && <Tile to={`/u/${unit.id}/groups`} title="Workpacks" detail="Groups of equipment, lines and E&I items" />}
          {session.can(Perm.excelPage) && <Tile to={`/u/${unit.id}/excel`} title="Excel" detail="Download the unit file, upload it back" />}
          {session.can(Perm.historyView) && <Tile to={`/u/${unit.id}/history`} title="History" detail={`${d.documentsPending} gate documents pending`} />}
        </section>
      )}
    </div>
  );
}

/** Key figures, activity bars, discipline table and the two category panels of one dashboard. */
function ScopeDashboard({ d, unitId, canEdit }: { d: Dashboard; unitId: number; canEdit: boolean }) {
  const byKeyword = d.categorySource === "Keywords";
  const prefix = d.scope === "ei" ? "ei-" : "";
  return (
    <>
      <section className="mt-6 grid grid-cols-2 gap-3 md:grid-cols-3 xl:grid-cols-6" aria-label="Key figures">
        <KeyFigures d={d} unitId={unitId} />
      </section>

      <section className="mt-6 rounded-lg border border-line bg-panel p-5">
        <div className="flex flex-wrap items-baseline justify-between gap-2">
          <h2 className="text-xl font-semibold">{d.scope === "ei" ? "Step progress" : "Activity progress"}</h2>
          <Legend scope={d.scope} />
        </div>
        <ul className="mt-4 space-y-4">
          {d.activities.map((a) => <ActivityBar key={a.code} row={a} scope={d.scope} />)}
        </ul>
      </section>

      <section className="mt-6 rounded-lg border border-line bg-panel">
        <h2 className="px-5 pt-5 text-xl font-semibold">Discipline-wise status</h2>
        <div className="mt-3 overflow-x-auto">
          <DisciplineTable rows={d.disciplines} activities={d.activities} className={d.scope === "ei" ? "min-w-[56rem]" : "min-w-[40rem]"} />
        </div>
      </section>

      <div className="mt-6 grid gap-6 xl:grid-cols-2">
        <CategoryPanel title="Hold categories" card={`${prefix}hold`} rows={d.hold} total={d.totalHold} unitId={unitId} scope={d.scope} canEdit={canEdit} byKeyword={byKeyword} />
        <CategoryPanel title="Not applicable categories" card={`${prefix}na`} rows={d.notApplicable} total={d.totalNotApplicable} unitId={unitId} scope={d.scope} canEdit={canEdit} byKeyword={byKeyword} />
      </div>
    </>
  );
}

/** Register (Equipment or E&I) showing exactly the equipment behind one dashboard figure. */
export const cardLink = (unitId: number, card: string, scope: DashboardScope = "mechanical") =>
  `/u/${unitId}/${scopeNames[scope].path}?card=${encodeURIComponent(card)}`;

/** Category editor of one catalogue. */
export const categoriesLink = (unitId: number, scope: DashboardScope) => `/u/${unitId}/categories${scope === "ei" ? "?scope=ei" : ""}`;

/**
 * The six key figures; the caller sets the grid. With a unit, each figure opens the register filtered on its
 * equipment. Also used by the PDF export (no unit: plain figures).
 */
export function KeyFigures({ d, unitId }: { d: Dashboard; unitId?: number }) {
  const register = scopeNames[d.scope].register;
  const to = (card: string) => (unitId ? cardLink(unitId, card, d.scope) : undefined);
  if (d.scope === "ei") {
    return (
      <>
        <Stat label="E&I tags" value={d.totalTags} detail="Electrical and instrumentation" to={to("ei-tags")} register={register} />
        <Stat label="Preserved" value={d.preserved} detail="All applicable steps E1–E8 completed" emphasis to={to("ei-preserved")} register={register} />
        <Stat label="E7 tests pending" value={d.e7Required - d.e7Complete} detail={d.e7Required === 0 ? "No motor marked for E7" : `${d.e7Complete} of ${d.e7Required} IR / winding tests complete`} to={to("ei-e7")} register={register} />
        <Stat label="On hold" value={d.totalHold} detail="By hold category" to={to("ei-hold")} register={register} />
        <Stat label="Not applicable" value={d.totalNotApplicable} detail="By N/A category" to={to("ei-na")} register={register} />
        <Stat label="Available" value={d.availableForPreservation} detail="Tags minus categories" to={to("ei-available")} register={register} />
      </>
    );
  }
  return (
    <>
      <Stat label="Equipment tags" value={d.totalTags} detail="Static and rotating" to={to("tags")} register={register} />
      <Stat label="Preserved" value={d.preserved} detail="G4 N2 blanketing completed" emphasis to={to("preserved")} register={register} />
      <Stat label="Ready for preservation" value={d.readyForPreservation} detail="Ready For Preservation = Yes" to={to("ready")} register={register} />
      <Stat label="On hold" value={d.totalHold} detail="By hold category" to={to("hold")} register={register} />
      <Stat label="Not applicable" value={d.totalNotApplicable} detail="By N/A category" to={to("na")} register={register} />
      <Stat label="Available" value={d.availableForPreservation} detail="Tags minus categories" to={to("available")} register={register} />
    </>
  );
}

/** Discipline-wise status table, one column per activity (Walkdown, G1–G4 or E1–E8). Also used by the PDF export. */
export function DisciplineTable({ rows, activities, className = "" }: { rows: Dashboard["disciplines"]; activities: ActivityRow[]; className?: string }) {
  const headers = ["Discipline", "Total tags", ...activities.map((a) => a.code), "EU preserved", "Overall progress"];
  return (
    <table className={`w-full text-left ${className}`}>
      <thead className="text-sm text-muted">
        <tr>
          {headers.map((h, i) => (
            <th key={h} className={`border-b border-line px-3 py-2 font-semibold ${i > 0 ? "text-right" : ""}`} title={activities.find((a) => a.code === h)?.label}>{h}</th>
          ))}
        </tr>
      </thead>
      <tbody>
        {rows.map((r) => (
          <tr key={r.discipline} className="border-b border-line last:border-0">
            <td className="px-3 py-2.5 font-semibold text-ink">{r.discipline}</td>
            {[r.total, ...r.done, r.euPreserved].map((n, i) => (
              <td key={i} className="px-3 py-2.5 text-right tabular-nums">{n}</td>
            ))}
            <td className="px-3 py-2.5">
              <div className="flex items-center justify-end gap-2">
                <span className="h-2 w-16 rounded-full bg-surface">
                  <span className="block h-2 rounded-full bg-violet" style={{ width: `${Math.min(100, r.progressPct)}%` }} />
                </span>
                <span className="w-12 text-right tabular-nums">{r.progressPct}%</span>
              </div>
            </td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}

/** The line under a category name (action by, e-mail action, recommendation, note, or its keywords). */
export function categoryDetail(c: CategoryRow, byKeyword: boolean) {
  return [c.actionBy && `Action by ${c.actionBy}`, c.emailAction, c.recommendation, c.note].filter(Boolean).join(" · ") ||
    (byKeyword && c.keywords.length > 0 ? `Keywords: ${c.keywords.join(", ")}` : "");
}

function Stat({ label, value, detail, emphasis = false, to, register }: { label: string; value: number; detail: string; emphasis?: boolean; to?: string; register: string }) {
  const body = (
    <>
      <p className="text-sm font-semibold text-muted">{label}</p>
      <p className="font-display text-[2.25rem] leading-none font-bold text-ink tabular-nums">{value}</p>
      <p className="mt-1 text-sm text-muted">{detail}</p>
    </>
  );
  const look = `rounded-lg border p-4 ${emphasis ? "border-violet/30 bg-violet-soft" : "border-line bg-panel"}`;
  if (!to) return <div className={look}>{body}</div>;
  return (
    <Link to={to} title={`Open these ${value} in the ${register}`} className={`${look} block transition hover:border-violet hover:shadow-sm focus-visible:outline-2 focus-visible:outline-violet`}>
      {body}
    </Link>
  );
}

type SegmentKey = "completed" | "pending" | "hold" | "notApplicable" | "other";

/**
 * Bar segments. The mechanical dashboard keeps N/A inside "N/A or other" like its Excel sheet; the E&I one shows
 * Not Applicable apart, because those steps are left out of its completion %.
 */
const segmentsOf = (scope: DashboardScope): { key: SegmentKey; label: string; color: string }[] => [
  { key: "completed", label: "Completed", color: "bg-done" },
  { key: "pending", label: "Pending", color: "bg-ongoing" },
  { key: "hold", label: "Hold", color: "bg-hold" },
  ...(scope === "ei"
    ? [{ key: "notApplicable" as const, label: "Not applicable", color: "bg-na/25" }, { key: "other" as const, label: "Not started", color: "bg-na/55" }]
    : [{ key: "other" as const, label: "N/A or other", color: "bg-na/45" }]),
];

export function Legend({ scope = "mechanical" }: { scope?: DashboardScope }) {
  return (
    <ul className="flex flex-wrap gap-x-4 gap-y-1 text-sm text-muted">
      {segmentsOf(scope).map((s) => (
        <li key={s.key} className="flex items-center gap-1.5"><span className={`size-2.5 rounded-sm ${s.color}`} aria-hidden />{s.label}</li>
      ))}
    </ul>
  );
}

/** One stacked bar per activity; the numbers are always written next to it, so colour never carries meaning alone. */
export function ActivityBar({ row, scope = "mechanical" }: { row: ActivityRow; scope?: DashboardScope }) {
  const segments = segmentsOf(scope);
  const visible = segments.filter((s) => row[s.key] > 0);
  const ei = scope === "ei";
  const counts = ei
    ? `${row.completed} done · ${row.pending} pending · ${row.hold} hold · ${row.notApplicable} N/A · ${row.other} not started`
    : `${row.completed} done · ${row.pending} pending · ${row.hold} hold · ${row.other} N/A or other`;
  return (
    <li>
      <div className="flex flex-wrap items-baseline justify-between gap-x-4">
        <span className="font-display text-lg font-semibold text-ink">{row.label}</span>
        <span className="text-sm text-muted tabular-nums">
          <strong className="text-text">{row.completionPct}%</strong> completed{ei && row.notApplicable > 0 ? " (of applicable)" : ""} · {counts}
        </span>
      </div>
      <div className="mt-1.5 flex h-3 gap-0.5" role="img" aria-label={`${row.label}: ${counts}, of ${row.total}`}>
        {visible.map((s, i) => (
          <span
            key={s.key}
            title={`${s.label}: ${row[s.key]}`}
            className={`${s.color} h-3 ${i === 0 ? "rounded-l" : ""} ${i === visible.length - 1 ? "rounded-r" : ""}`}
            style={{ width: `${(row[s.key] / Math.max(1, row.total)) * 100}%` }}
          />
        ))}
      </div>
    </li>
  );
}

function CategoryPanel({ title, card, rows, total, unitId, scope, canEdit, byKeyword }: {
  title: string; card: string; rows: CategoryRow[]; total: number; unitId: number; scope: DashboardScope; canEdit: boolean; byKeyword: boolean;
}) {
  const [open, setOpen] = useState<number | null>(null);
  const register = scopeNames[scope].register;
  return (
    <section className="rounded-lg border border-line bg-panel">
      <div className="flex items-baseline justify-between gap-3 px-5 pt-5">
        <h2 className="text-xl font-semibold">{title}</h2>
        {canEdit && <Link to={categoriesLink(unitId, scope)} className="text-sm font-semibold text-violet">Edit categories</Link>}
      </div>
      {rows.length === 0 ? (
        <p className="px-5 pt-2 pb-5 text-muted">No categories yet.{canEdit ? " Add them under Edit categories." : ""}</p>
      ) : (
        <ul className="mt-3">
          {rows.map((c) => (
            <li key={c.id} className="border-t border-line">
              <button
                type="button"
                onClick={() => setOpen(open === c.id ? null : c.id)}
                className="grid w-full grid-cols-[1fr_auto] items-center gap-3 px-5 py-3 text-left hover:bg-surface"
                aria-expanded={open === c.id}
              >
                <span>
                  <span className="block font-semibold text-ink">
                    {c.code && <span className="mr-2 text-hold">{c.code}</span>}
                    {c.label}
                  </span>
                  <span className="block text-sm text-muted">
                    {categoryDetail(c, byKeyword)}
                  </span>
                </span>
                <span className="font-display text-2xl font-bold text-ink tabular-nums">{c.count}</span>
              </button>
              {open === c.id && (
                <div className="px-5 pb-4">
                  {c.assets.length > 0 && (
                    <Link to={cardLink(unitId, `category-${c.id}`, scope)} className="mb-2 inline-block text-sm font-semibold text-violet">
                      Open these {c.count} in the {register} →
                    </Link>
                  )}
                  {c.assets.length === 0 ? (
                    <p className="text-sm text-muted">
                      {scope === "ei" ? "No E&I item in this category." : "No equipment in this category."}
                      {byKeyword ? ` Keywords searched in Preservation Remarks: ${c.keywords.join(", ") || "none"}.` : ` Assign it from the ${scope === "ei" ? "item" : "equipment"} page.`}
                    </p>
                  ) : (
                    <ul className="flex flex-wrap gap-1.5">
                      {c.assets.map((a) => (
                        <li key={a.id}>
                          <Link to={`/assets/${a.id}`} className="inline-block rounded-md border border-line px-2 py-0.5 text-sm font-semibold text-ink hover:border-violet hover:text-violet">{a.tag ?? a.code}</Link>
                        </li>
                      ))}
                    </ul>
                  )}
                </div>
              )}
            </li>
          ))}
          <li className="border-t border-line">
            <Link to={cardLink(unitId, card, scope)} className="flex justify-between px-5 py-3 font-semibold hover:bg-surface hover:text-violet" title={`Open in the ${register}`}>
              <span>Total</span>
              <span className="tabular-nums">{total}</span>
            </Link>
          </li>
        </ul>
      )}
    </section>
  );
}

function Tile({ to, title, detail }: { to: string; title: string; detail: string }) {
  return (
    <Link to={to} className="block rounded-lg border border-line bg-panel px-4 py-3 hover:border-violet">
      <span className="block font-display text-lg font-semibold text-ink">{title}</span>
      <span className="text-sm text-muted">{detail}</span>
    </Link>
  );
}
