import { useEffect, useState } from "react";
import { Link, useParams } from "react-router";
import { useQuery } from "@tanstack/react-query";
import { api } from "../../api/client";
import { Perm, type ActivityRow, type CategoryRow, type Dashboard } from "../../api/types";
import { lastUnit, useUnit } from "../../app/useCatalog";
import { useSession } from "../../app/session";
import { PageHeader } from "../../components/PageHeader";
import { DownloadExcelButton } from "../excel/DownloadExcelButton";

/**
 * Unit overview = the client dashboard, computed by the API exactly like the Dashboard sheet of the unit Excel
 * (static and rotating equipment; Electrical, Instrumentation and E&I excluded), so both always show the same numbers.
 */
export function UnitHomePage() {
  const unitId = Number(useParams().unitId);
  const { unit, plantName, isPending } = useUnit(unitId);
  const session = useSession();
  const dashboard = useQuery({
    queryKey: ["dashboard", unitId],
    queryFn: ({ signal }) => api<Dashboard>(`/api/units/${unitId}/dashboard`, { signal }),
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

  const d = dashboard.data;
  return (
    <div>
      <PageHeader
        title={unit.name}
        description={`${plantName} · preservation dashboard`}
        actions={
          <>
            {session.can(Perm.excelExport) && <DownloadExcelButton unitId={unit.id} variant="secondary" />}
            {session.can(Perm.assetsManage) && (
              <Link to={`/u/${unit.id}/new`} className="inline-flex min-h-10 items-center rounded-md bg-violet px-4 font-semibold text-white hover:bg-violet-dark">New equipment</Link>
            )}
          </>
        }
      />

      {dashboard.isPending && <p className="mt-6 text-muted">Loading dashboard…</p>}
      {dashboard.isError && <p className="mt-6 text-hold">{dashboard.error.message}</p>}

      {d && d.totalTags === 0 && (
        <div className="mt-8 max-w-xl rounded-lg border border-dashed border-line bg-panel p-6">
          <h2 className="text-xl font-semibold">No equipment in this unit yet</h2>
          <p className="mt-2 text-muted">Equipment arrives with the migration, an Onboarding list on the Excel page, or “New equipment”.</p>
        </div>
      )}

      {d && d.totalTags > 0 && (
        <>
          <section className="mt-6 grid grid-cols-2 gap-3 md:grid-cols-3 xl:grid-cols-6" aria-label="Key figures">
            <Stat label="Equipment tags" value={d.totalTags} detail="Static and rotating" />
            <Stat label="Preserved" value={d.preserved} detail="G4 N2 blanketing completed" emphasis />
            <Stat label="Ready for preservation" value={d.readyForPreservation} detail="G4 ongoing" />
            <Stat label="On hold" value={d.totalHold} detail="By hold category" />
            <Stat label="Not applicable" value={d.totalNotApplicable} detail="By N/A category" />
            <Stat label="Available" value={d.availableForPreservation} detail="Tags minus categories" />
          </section>

          <section className="mt-6 rounded-lg border border-line bg-panel p-5">
            <div className="flex flex-wrap items-baseline justify-between gap-2">
              <h2 className="text-xl font-semibold">Activity progress</h2>
              <Legend />
            </div>
            <ul className="mt-4 space-y-4">
              {d.activities.map((a) => <ActivityBar key={a.label} row={a} />)}
            </ul>
          </section>

          <section className="mt-6 rounded-lg border border-line bg-panel">
            <h2 className="px-5 pt-5 text-xl font-semibold">Discipline-wise status</h2>
            <div className="mt-3 overflow-x-auto">
              <table className="w-full min-w-[40rem] text-left">
                <thead className="text-sm text-muted">
                  <tr>
                    {["Discipline", "Total tags", "Walkdown", "G1", "G2", "G3", "G4", "EU preserved", "Overall progress"].map((h, i) => (
                      <th key={h} className={`border-b border-line px-4 py-2 font-semibold ${i > 0 ? "text-right" : ""}`}>{h}</th>
                    ))}
                  </tr>
                </thead>
                <tbody>
                  {d.disciplines.map((r) => (
                    <tr key={r.discipline} className="border-b border-line last:border-0">
                      <td className="px-4 py-2.5 font-semibold text-ink">{r.discipline}</td>
                      {[r.total, r.walkdown, r.g1, r.g2, r.g3, r.g4, r.euPreserved].map((n, i) => (
                        <td key={i} className="px-4 py-2.5 text-right tabular-nums">{n}</td>
                      ))}
                      <td className="px-4 py-2.5">
                        <div className="flex items-center justify-end gap-2">
                          <span className="h-2 w-20 rounded-full bg-surface">
                            <span className="block h-2 rounded-full bg-violet" style={{ width: `${Math.min(100, r.progressPct)}%` }} />
                          </span>
                          <span className="w-12 text-right tabular-nums">{r.progressPct}%</span>
                        </div>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </section>

          <div className="mt-6 grid gap-6 xl:grid-cols-2">
            <CategoryPanel title="Hold categories" rows={d.hold} total={d.totalHold} unitId={unit.id} canEdit={session.can(Perm.categoriesManage)} />
            <CategoryPanel title="Not applicable categories" rows={d.notApplicable} total={d.totalNotApplicable} unitId={unit.id} canEdit={session.can(Perm.categoriesManage)} />
          </div>

          <section className="mt-6 grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
            <Tile to={`/u/${unit.id}/equipment`} title="Equipment Register" detail={`${Object.values(d.byCurrentGate).reduce((a, b) => a + b, 0)} mechanical equipment`} />
            <Tile to={`/u/${unit.id}/lines`} title="Pipeline Register" detail={d.lineCount === 0 ? "No lines yet" : `${d.lineCount} lines · ${Math.round(d.lineLengthM).toLocaleString("en")} m · ${d.linesBlanketed} N2 blanketed`} />
            <Tile to={`/u/${unit.id}/ei`} title="E&I Register" detail={`${d.electricalInstrumentCount} electrical and instrumentation items`} />
            <Tile to={`/u/${unit.id}/monitoring`} title="Monitoring" detail="N2 blanket checks during and after preservation" />
            <Tile to={`/u/${unit.id}/groups`} title="Workpacks" detail="Groups of equipment, lines and E&I items" />
            {session.can(Perm.excelPage) && <Tile to={`/u/${unit.id}/excel`} title="Excel" detail="Download the unit file, upload it back" />}
            <Tile to={`/u/${unit.id}/history`} title="History" detail={`${d.documentsPending} gate documents pending`} />
          </section>
        </>
      )}
    </div>
  );
}

function Stat({ label, value, detail, emphasis = false }: { label: string; value: number; detail: string; emphasis?: boolean }) {
  return (
    <div className={`rounded-lg border p-4 ${emphasis ? "border-violet/30 bg-violet-soft" : "border-line bg-panel"}`}>
      <p className="text-sm font-semibold text-muted">{label}</p>
      <p className="font-display text-[2.25rem] leading-none font-bold text-ink tabular-nums">{value}</p>
      <p className="mt-1 text-sm text-muted">{detail}</p>
    </div>
  );
}

const segments = [
  { key: "completed", label: "Completed", color: "bg-done" },
  { key: "pending", label: "Pending", color: "bg-ongoing" },
  { key: "hold", label: "Hold", color: "bg-hold" },
  { key: "other", label: "N/A or other", color: "bg-na/45" },
] as const;

function Legend() {
  return (
    <ul className="flex flex-wrap gap-x-4 gap-y-1 text-sm text-muted">
      {segments.map((s) => (
        <li key={s.key} className="flex items-center gap-1.5"><span className={`size-2.5 rounded-sm ${s.color}`} aria-hidden />{s.label}</li>
      ))}
    </ul>
  );
}

/** One stacked bar per activity; the numbers are always written next to it, so colour never carries meaning alone. */
function ActivityBar({ row }: { row: ActivityRow }) {
  const visible = segments.filter((s) => row[s.key] > 0);
  return (
    <li>
      <div className="flex flex-wrap items-baseline justify-between gap-x-4">
        <span className="font-display text-lg font-semibold text-ink">{row.label}</span>
        <span className="text-sm text-muted tabular-nums">
          <strong className="text-text">{row.completionPct}%</strong> completed · {row.completed} done · {row.pending} pending · {row.hold} hold · {row.other} N/A or other
        </span>
      </div>
      <div className="mt-1.5 flex h-3 gap-0.5" role="img" aria-label={`${row.label}: ${row.completed} completed, ${row.pending} pending, ${row.hold} hold, ${row.other} other, of ${row.total}`}>
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

function CategoryPanel({ title, rows, total, unitId, canEdit }: { title: string; rows: CategoryRow[]; total: number; unitId: number; canEdit: boolean }) {
  const [open, setOpen] = useState<number | null>(null);
  return (
    <section className="rounded-lg border border-line bg-panel">
      <div className="flex items-baseline justify-between gap-3 px-5 pt-5">
        <h2 className="text-xl font-semibold">{title}</h2>
        {canEdit && <Link to={`/u/${unitId}/categories`} className="text-sm font-semibold text-violet">Edit categories</Link>}
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
                  <span className="block font-semibold text-ink">{c.label}</span>
                  <span className="block text-sm text-muted">
                    {[c.actionBy && `Action by ${c.actionBy}`, c.emailAction, c.note].filter(Boolean).join(" · ") || `Keywords: ${c.keywords.join(", ")}`}
                  </span>
                </span>
                <span className="font-display text-2xl font-bold text-ink tabular-nums">{c.count}</span>
              </button>
              {open === c.id && (
                <div className="px-5 pb-4">
                  {c.assets.length === 0 ? (
                    <p className="text-sm text-muted">No equipment in this category. Keywords searched in Preservation Remarks: {c.keywords.join(", ")}.</p>
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
          <li className="flex justify-between border-t border-line px-5 py-3 font-semibold">
            <span>Total</span>
            <span className="tabular-nums">{total}</span>
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
