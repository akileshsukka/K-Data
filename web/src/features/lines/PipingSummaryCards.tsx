import { useState, type ReactNode } from "react";
import { Link } from "react-router";
import { useQuery } from "@tanstack/react-query";
import { api } from "../../api/client";
import type { PipingCount, PipingSummary } from "../../api/types";

const pct = (c: PipingCount) => (c.total > 0 ? Math.round((c.done / c.total) * 100) : 0);

/** Card of the piping summary that opens the Pipeline Register on its lines (with "pending": the ones not done yet). */
export type PipingCard = "walkdown" | "drained" | "inspected" | "scope" | "cleaned";

/** Pipeline Register showing exactly the lines behind one card (?card=piping-walkdown, piping-walkdown-pending…). */
export const pipingCardLink = (unitId: number, card: PipingCard, pending = false) =>
  `/u/${unitId}/lines?card=piping-${card}${pending ? "-pending" : ""}`;

/**
 * The piping Summary of the line list as cards: walkdown, draining, inspection, cleaning scope and cleaning done
 * (against the scope), with a table per service code on demand. Same rules as the Summary sheet of the Excel.
 */
export function PipingSummaryCards({ unitId, title = "Piping status", grouped }: { unitId: number; title?: string; /** Not given: the unit's default view. */ grouped?: boolean }) {
  const [open, setOpen] = useState(false);
  const q = useQuery({
    queryKey: ["piping-summary", unitId, grouped ?? "default"],
    queryFn: ({ signal }) => api<PipingSummary>(`/api/units/${unitId}/piping-summary${grouped === undefined ? "" : `?grouped=${grouped}`}`, { signal }),
  });
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
        <PipingCards s={s} unitId={unitId} />
      </div>
      {s.grouped && (
        <p className="mt-2 text-sm text-muted">Sizes of one line count as one; a line is done when all its sizes are.</p>
      )}
      {open && (
        <div className="mt-3 overflow-x-auto rounded-lg border border-line bg-panel">
          <PipingServiceTable rows={s.byService} className="min-w-[44rem]" />
        </div>
      )}
    </section>
  );
}

/**
 * The six cards (lines, walkdown, drained, inspected, cleaning scope, cleaned); the caller sets the grid. With a unit, a card opens the
 * Pipeline Register on its lines and "N pending" on the ones not done yet; the API lists them with the same rule that counts them.
 * Also used by the PDF export (no unit: plain figures).
 */
export function PipingCards({ s, unitId }: { s: PipingSummary; unitId?: number }) {
  const scope = s.cleaning.filter((c) => c.inScope).reduce((a, c) => a + c.lines, 0);
  const link = (card: PipingCard, total: number) =>
    unitId !== undefined && total > 0 ? (pending = false) => pipingCardLink(unitId, card, pending) : undefined;
  return (
    <>
      <div className="rounded-lg border border-line bg-panel p-4">
        <p className="text-sm font-semibold text-muted">{s.grouped ? "Lines (grouped)" : "Lines"}</p>
        <p className="font-display text-[2.25rem] leading-none font-bold text-ink tabular-nums">{s.lines}</p>
        <p className="mt-1 text-sm text-muted">
          {[s.grouped && s.sections !== s.lines && `${s.sections} sizes`, s.lengthM > 0 && `${Math.round(s.lengthM).toLocaleString("en")} m`].filter(Boolean).join(" · ") || "Active lines"}
        </p>
      </div>
      <Progress label="Walkdown" count={s.walkdown} detail="EM walkdown completed" to={link("walkdown", s.walkdown.total)} />
      <Progress label="Drained" count={s.drained} detail="Bulk liquid drained" to={link("drained", s.drained.total)} />
      <Progress label="Inspected" count={s.inspected} detail="Inspection done" pendingNote="N/A left out" to={link("inspected", s.inspected.total)} />
      <Card to={link("scope", scope)?.()} title={`Open the ${scope} lines of the cleaning scope in the Pipeline Register`}>
        <p className="text-sm font-semibold text-muted">Cleaning scope</p>
        <p className="font-display text-[2.25rem] leading-none font-bold text-ink tabular-nums">{scope}</p>
        <p className="mt-1 text-sm text-muted">
          {s.cleaning.map((c) => `${c.lines} ${c.value}`).join(" · ") || "No values"}
          {s.noCleaningSet > 0 && ` · ${s.noCleaningSet} not set`}
        </p>
      </Card>
      <Progress label="Cleaned" count={s.cleaned} detail={s.cleaned.total > 0 ? "of the cleaning scope" : "No lines to clean yet"} pendingNote="in scope, not cleaned" emphasis
        to={link("cleaned", s.cleaned.total)} />
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
            <Cell done={r.inspected} total={r.inspectionTotal} />
            <td className="px-4 py-2 text-right tabular-nums">{r.scope || "—"}</td>
            <Cell done={r.cleaned} total={r.scope} />
          </tr>
        ))}
      </tbody>
    </table>
  );
}

/**
 * A card. With a link, the whole card opens it: a stretched link behind the content, so a second link ("N pending") can sit on top
 * (links cannot be nested).
 */
function Card({ to, title, emphasis = false, children }: { to?: string; title?: string; emphasis?: boolean; children: ReactNode }) {
  const look = `rounded-lg border p-4 ${emphasis ? "border-violet/30 bg-violet-soft" : "border-line bg-panel"}`;
  if (!to) return <div className={look}>{children}</div>;
  return (
    <div className={`${look} relative transition hover:border-violet hover:shadow-sm has-[.card-main:focus-visible]:outline-2 has-[.card-main:focus-visible]:outline-violet`}>
      <Link to={to} title={title} aria-label={title} className="card-main absolute inset-0 rounded-lg focus-visible:outline-none" />
      {children}
    </div>
  );
}

function Progress({ label, count, detail, emphasis = false, to, pendingNote }: {
  label: string; count: PipingCount; detail: string; emphasis?: boolean;
  /** Register link of the card (lines done) and, with pending = true, of the ones not done yet. */
  to?: (pending?: boolean) => string;
  /** What "pending" leaves out, said in its tooltip (e.g. "N/A left out"). */
  pendingNote?: string;
}) {
  const p = pct(count);
  const pending = count.total - count.done;
  return (
    <Card to={to?.()} title={`Open the ${count.done} lines done (${label}) in the Pipeline Register`} emphasis={emphasis}>
      <p className="text-sm font-semibold text-muted">{label}</p>
      <p className="font-display text-[2.25rem] leading-none font-bold text-ink tabular-nums">
        {count.total > 0 ? `${p}%` : "—"}
      </p>
      <div className="mt-2 h-1.5 rounded-full bg-surface" role="img" aria-label={`${label}: ${count.done} of ${count.total}`}>
        <span className="block h-1.5 rounded-full bg-done" style={{ width: `${p}%` }} />
      </div>
      <p className="mt-1 text-sm text-muted tabular-nums">{count.done} of {count.total} · {detail}</p>
      {to && pending > 0 && (
        <Link to={to(true)} className="relative mt-1 inline-block text-sm font-semibold text-violet hover:underline"
          title={`Open the ${pending} lines still pending (${label}) in the Pipeline Register${pendingNote ? `: ${pendingNote}` : ""}`}>
          {pending} pending →
        </Link>
      )}
    </Card>
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
