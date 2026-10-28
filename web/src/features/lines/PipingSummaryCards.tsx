import { useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { api } from "../../api/client";
import type { PipingCount, PipingSummary } from "../../api/types";

const pct = (c: PipingCount) => (c.total > 0 ? Math.round((c.done / c.total) * 100) : 0);

/**
 * The piping Summary of the line list as cards: walkdown, draining, inspection, cleaning scope and cleaning done
 * (against the scope), with a table per service code on demand. Same rules as the Summary sheet of the Excel.
 */
export function PipingSummaryCards({ unitId, title = "Piping status" }: { unitId: number; title?: string }) {
  const [open, setOpen] = useState(false);
  const q = useQuery({ queryKey: ["piping-summary", unitId], queryFn: ({ signal }) => api<PipingSummary>(`/api/units/${unitId}/piping-summary`, { signal }) });
  const s = q.data;
  // Say so instead of hiding the section: a role without access must be visible, not silently missing.
  if (q.isError) return <p className="mt-6 text-hold">{title}: {q.error.message}</p>;
  if (!s || s.lines === 0) return null;

  return (
    <section className="mt-6" aria-label={title}>
      <div className="flex flex-wrap items-baseline justify-between gap-2">
        <h2 className="text-xl font-semibold">{title}</h2>
        <button className="text-sm font-semibold text-violet" onClick={() => setOpen(!open)} aria-expanded={open}>{open ? "Hide by service" : "Show by service"}</button>
      </div>
      <div className="mt-3 grid grid-cols-2 gap-3 md:grid-cols-3 xl:grid-cols-6">
        <PipingCards s={s} />
      </div>
      {open && (
        <div className="mt-3 overflow-x-auto rounded-lg border border-line bg-panel">
          <PipingServiceTable rows={s.byService} className="min-w-[44rem]" />
        </div>
      )}
    </section>
  );
}

/** The six cards (lines, walkdown, drained, inspected, cleaning scope, cleaned); the caller sets the grid. Also used by the PDF export. */
export function PipingCards({ s }: { s: PipingSummary }) {
  const scope = s.cleaning.filter((c) => c.inScope).reduce((a, c) => a + c.lines, 0);
  return (
    <>
      <div className="rounded-lg border border-line bg-panel p-4">
        <p className="text-sm font-semibold text-muted">Lines</p>
        <p className="font-display text-[2.25rem] leading-none font-bold text-ink tabular-nums">{s.lines}</p>
        <p className="mt-1 text-sm text-muted">{s.lengthM > 0 ? `${Math.round(s.lengthM).toLocaleString("en")} m` : "Active lines"}</p>
      </div>
      <Progress label="Walkdown" count={s.walkdown} detail="EM walkdown completed" />
      <Progress label="Drained" count={s.drained} detail="Bulk liquid drained" />
      <Progress label="Inspected" count={s.inspected} detail="Inspection done" />
      <div className="rounded-lg border border-line bg-panel p-4">
        <p className="text-sm font-semibold text-muted">Cleaning scope</p>
        <p className="font-display text-[2.25rem] leading-none font-bold text-ink tabular-nums">{scope}</p>
        <p className="mt-1 text-sm text-muted">
          {s.cleaning.map((c) => `${c.lines} ${c.value}`).join(" · ") || "No values"}
          {s.noCleaningSet > 0 && ` · ${s.noCleaningSet} not set`}
        </p>
      </div>
      <Progress label="Cleaned" count={s.cleaned} detail={s.cleaned.total > 0 ? "of the cleaning scope" : "No lines to clean yet"} emphasis />
    </>
  );
}

/** Lines per service code with each activity done (and %). Also used by the PDF export. */
export function PipingServiceTable({ rows, className = "" }: { rows: PipingSummary["byService"]; className?: string }) {
  return (
    <table className={`w-full text-left text-[15px] ${className}`}>
      <thead className="text-sm text-muted">
        <tr>
          {["Service", "Lines", "Walkdown", "Drained", "Inspected", "Cleaning scope", "Cleaned"].map((h, i) => (
            <th key={h} className={`border-b border-line px-4 py-2 font-semibold ${i > 0 ? "text-right" : ""}`}>{h}</th>
          ))}
        </tr>
      </thead>
      <tbody>
        {rows.map((r) => (
          <tr key={r.code} className="border-b border-line last:border-0">
            <td className="px-4 py-2">
              <span className="font-semibold text-ink">{r.code || "—"}</span>
              {r.description && <span className="ml-2 text-sm text-muted">{r.description}</span>}
            </td>
            <td className="px-4 py-2 text-right tabular-nums">{r.lines}</td>
            <Cell done={r.walkdown} total={r.lines} />
            <Cell done={r.drained} total={r.lines} />
            <Cell done={r.inspected} total={r.lines} />
            <td className="px-4 py-2 text-right tabular-nums">{r.scope || "—"}</td>
            <Cell done={r.cleaned} total={r.scope} />
          </tr>
        ))}
      </tbody>
    </table>
  );
}

function Progress({ label, count, detail, emphasis = false }: { label: string; count: PipingCount; detail: string; emphasis?: boolean }) {
  const p = pct(count);
  return (
    <div className={`rounded-lg border p-4 ${emphasis ? "border-violet/30 bg-violet-soft" : "border-line bg-panel"}`}>
      <p className="text-sm font-semibold text-muted">{label}</p>
      <p className="font-display text-[2.25rem] leading-none font-bold text-ink tabular-nums">
        {count.total > 0 ? `${p}%` : "—"}
      </p>
      <div className="mt-2 h-1.5 rounded-full bg-surface" role="img" aria-label={`${label}: ${count.done} of ${count.total}`}>
        <span className="block h-1.5 rounded-full bg-done" style={{ width: `${p}%` }} />
      </div>
      <p className="mt-1 text-sm text-muted tabular-nums">{count.done} of {count.total} · {detail}</p>
    </div>
  );
}

function Cell({ done, total }: { done: number; total: number }) {
  if (total === 0) return <td className="px-4 py-2 text-right text-muted">—</td>;
  return (
    <td className="px-4 py-2 text-right tabular-nums">
      {done} <span className="text-sm text-muted">({Math.round((done / total) * 100)}%)</span>
    </td>
  );
}
