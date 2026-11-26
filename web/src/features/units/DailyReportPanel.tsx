import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api, ApiError } from "../../api/client";
import { Perm } from "../../api/types";
import { useSession } from "../../app/session";
import { Button } from "../../components/Button";
import { useToast } from "../../components/Toast";
import { formatDate, todayDubai } from "../../components/dates";
import { exportDailyCardPdf } from "./dailyCardPdf";

const PIPING_KEY = "kdata.dailyCard.showPiping";

interface Highlight {
  id: number;
  date: string;
  text: string;
  updatedBy: string;
  updatedAtUtc: string;
}

interface DailyUnit {
  unitId: number;
  applicable: number;
  preserved: number;
  preservedPct: number;
  preservedPctDayBefore: number;
  /** The % the reports show (26-nov): gate steps completed ÷ gate steps, as the "Overall progress" of the dashboards. */
  gateProgressPct: number;
  gateProgressPctDayBefore: number;
  completedTags: string[];
  completedByDiscipline: { discipline: string; tags: string[] }[];
  lines: number;
  linesPreserved: number;
  highlights: Highlight[];
}

interface DailySummary {
  date: string;
  units: DailyUnit[];
}

/** yyyy-MM-dd moved by whole days. */
const shift = (day: string, days: number) => {
  const d = new Date(`${day}T00:00:00Z`);
  d.setUTCDate(d.getUTCDate() + days);
  return d.toISOString().slice(0, 10);
};

const pct = (n: number) => `${Number.isInteger(n) ? n : n.toFixed(1)}%`;

/**
 * What this unit gives to the daily report of one site day (the day ends at midnight UAE time): preserved at the end of
 * the day, its change, the tags completed that day, and the highlights the engineers write here. Same counting as the
 * dashboards below; folded by default.
 */
export function DailyReportPanel({ unitId }: { unitId: number }) {
  const session = useSession();
  const toast = useToast();
  const client = useQueryClient();
  const today = todayDubai();
  const [open, setOpen] = useState(false);
  const [day, setDay] = useState(today);
  const [draft, setDraft] = useState<{ id: number | null; text: string; expected: string | null } | null>(null);
  const canEdit = session.can(Perm.reportHighlights);
  const [cardBusy, setCardBusy] = useState(false);
  // Piping lines in or out of the card PDF (temporary; remembered in this browser).
  const [showPiping, setShowPiping] = useState(() => { try { return localStorage.getItem(PIPING_KEY) !== "0"; } catch { return true; } });
  const togglePiping = (on: boolean) => { setShowPiping(on); try { localStorage.setItem(PIPING_KEY, on ? "1" : "0"); } catch { /* private mode */ } };
  const downloadCard = async () => {
    setCardBusy(true);
    try { toast(`Saved ${await exportDailyCardPdf(unitId, day, showPiping)}`); }
    catch (e) { toast(e instanceof Error ? e.message : "Could not make the PDF.", "error"); }
    finally { setCardBusy(false); }
  };

  const key = ["daily-report", unitId, day];
  const summary = useQuery({
    queryKey: key,
    queryFn: ({ signal }) => api<DailySummary>(`/api/reports/daily?date=${day}&unitId=${unitId}`, { signal }),
  });
  const u = summary.data?.units[0];
  const highlights = u?.highlights ?? [];

  const save = useMutation({
    mutationFn: (d: { id: number | null; text: string | null; expected: string | null; remove?: boolean }) =>
      d.remove
        ? api<Highlight[]>(`/api/units/${unitId}/highlights/${d.id}`, { method: "DELETE" })
        : d.id === null
          ? api<Highlight[]>(`/api/units/${unitId}/highlights`, { method: "POST", body: { date: day, text: d.text } })
          : api<Highlight[]>(`/api/units/${unitId}/highlights/${d.id}`, { method: "PUT", body: { text: d.text, expected: d.expected } }),
    onSuccess: (list, d) => {
      client.setQueryData<DailySummary>(key, (old) => old && { ...old, units: old.units.map((x) => ({ ...x, highlights: list })) });
      setDraft(null);
      toast(d.remove ? "Highlight removed" : "Highlight saved");
    },
    onError: (e) => toast(e instanceof ApiError ? e.message : "Could not save.", "error"),
  });

  const change = u ? u.gateProgressPct - u.gateProgressPctDayBefore : 0;
  const completed = u?.completedTags ?? [];
  const summaryLine = !u
    ? summary.isError ? "could not load" : "loading…"
    : `${pct(u.gateProgressPct)} progress · ${u.preserved} preserved · ${completed.length} completed · ${highlights.length} highlight${highlights.length === 1 ? "" : "s"}`;

  return (
    <section className="mt-4 rounded-lg border border-line bg-panel">
      <div className="flex flex-wrap items-center justify-between gap-3 px-4 py-2.5">
        <button className="min-w-0 text-left" onClick={() => setOpen(!open)} aria-expanded={open}>
          <span className="font-display text-lg font-semibold text-ink">Daily report</span>
          <span className="ml-2 text-sm text-muted">{formatDate(day)} · {summaryLine}</span>
        </button>
        <div className="flex items-center gap-1">
          <Button variant="quiet" aria-label="Day before" onClick={() => { setDay(shift(day, -1)); setDraft(null); }}>‹</Button>
          <Button variant="quiet" disabled={day === today} onClick={() => { setDay(today); setDraft(null); }}>Today</Button>
          <Button variant="quiet" aria-label="Next day" disabled={day >= today} onClick={() => { setDay(shift(day, 1)); setDraft(null); }}>›</Button>
          <label className="ml-2 inline-flex items-center gap-1.5 text-sm text-muted" title="Piping lines in the card PDF">
            <input type="checkbox" checked={showPiping} onChange={(e) => togglePiping(e.target.checked)} /> Piping
          </label>
          <Button variant="secondary" busy={cardBusy} onClick={downloadCard} title="The card of this unit as in the general daily report (with its activities from EPCS), as a PDF">Card PDF</Button>
          <Button variant="quiet" onClick={() => setOpen(!open)}>{open ? "Hide" : "Show"}</Button>
        </div>
      </div>
      {open && (
        <div className="space-y-4 border-t border-line px-4 py-3">
          {summary.isError && <p className="text-hold">{summary.error.message}</p>}
          {u && (
            <div className="flex flex-wrap items-baseline gap-x-6 gap-y-1">
              <p><span className="font-display text-2xl font-bold text-ink tabular-nums">{pct(u.gateProgressPct)}</span> <span className="text-muted">{u.preserved} of {u.applicable} preserved</span></p>
              <p className={change > 0 ? "font-semibold text-done" : "text-muted"}>{change > 0 ? `▲ ${Math.round(change * 10) / 10} pts on ${formatDate(day)}` : `No change on ${formatDate(day)}`}</p>
              {u.lines > 0 && <p className="text-muted">Piping: {u.linesPreserved} of {u.lines} lines preserved</p>}
            </div>
          )}
          {u && (
            <div>
              <h3 className="text-sm font-semibold uppercase tracking-wide text-muted">Completed on {formatDate(day)}</h3>
              {completed.length === 0
                ? <p className="mt-1 text-muted">No tags completed{day === today ? " yet today" : ""}.</p>
                : <div className="mt-1.5 space-y-1.5">
                    {u.completedByDiscipline.map((g) => (
                      <div key={g.discipline} className="flex flex-wrap items-center gap-x-2.5 gap-y-1.5">
                        <span className="min-w-36 text-sm font-semibold text-ink">{g.discipline} <span className="font-normal text-muted">({g.tags.length})</span></span>
                        <ul className="flex flex-wrap gap-1.5">{g.tags.map((t) => <li key={t} className="rounded bg-done/10 px-2 py-0.5 text-sm font-semibold text-done tabular-nums">{t}</li>)}</ul>
                      </div>
                    ))}
                  </div>}
            </div>
          )}
          <div>
            <h3 className="text-sm font-semibold uppercase tracking-wide text-muted">Highlights</h3>
            {highlights.length === 0 && !draft && <p className="mt-1 text-muted">None for {formatDate(day)}. Write only the important points of the day; they go to the daily report.</p>}
            <ul className="mt-1.5 space-y-2">
              {highlights.map((h) => (
                <li key={h.id} className="flex flex-wrap items-start justify-between gap-2">
                  <div className="min-w-0 flex-1">
                    <p className="whitespace-pre-line">{h.text}</p>
                    <p className="text-sm text-muted">by {h.updatedBy}</p>
                  </div>
                  {canEdit && !draft && <Button variant="quiet" onClick={() => setDraft({ id: h.id, text: h.text, expected: h.text })}>Edit</Button>}
                </li>
              ))}
            </ul>
            {draft && (
              <div className="mt-3 space-y-2 rounded-md border border-line p-3">
                <label className="block">
                  <span className="text-sm font-semibold text-ink">Highlight for {formatDate(day)}</span>
                  <textarea
                    value={draft.text}
                    rows={3}
                    maxLength={1000}
                    autoFocus
                    onChange={(e) => setDraft({ ...draft, text: e.target.value })}
                    className="mt-1 block w-full rounded-md border border-line bg-panel px-3 py-2 text-[15px] focus:border-violet focus:outline-none"
                  />
                </label>
                <div className="flex flex-wrap gap-2">
                  <Button busy={save.isPending} disabled={!draft.text.trim()} onClick={() => save.mutate({ id: draft.id, text: draft.text, expected: draft.expected })}>Save</Button>
                  {draft.id !== null && (
                    <Button variant="danger" busy={save.isPending} onClick={() => save.mutate({ id: draft.id, text: null, expected: draft.expected, remove: true })}>Remove</Button>
                  )}
                  <Button variant="secondary" onClick={() => setDraft(null)}>Cancel</Button>
                </div>
              </div>
            )}
            {canEdit && !draft && <Button variant="secondary" className="mt-3" onClick={() => setDraft({ id: null, text: "", expected: null })}>Add highlight</Button>}
          </div>
        </div>
      )}
    </section>
  );
}
