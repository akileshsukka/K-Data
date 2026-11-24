import { flushSync } from "react-dom";
import { createRoot } from "react-dom/client";
import { getFontEmbedCSS, toCanvas } from "html-to-image";
import { jsPDF } from "jspdf";
import { api } from "../../api/client";

/*
  One unit's daily card as a PDF: the same card as the general daily report of EPCS (same colours, table and activities),
  for one unit only. Drawn off screen, turned into one image and placed on an A4 portrait page.
*/

interface Discipline { discipline: string; tags: number; notApplicable: number; preserved: number; hold: number }
interface DailyUnit {
  unitNumber: number; unitName: string; plantName: string; applicable: number; preserved: number; hold: number;
  preservedPct: number; preservedPctDayBefore: number; disciplines: Discipline[]; completedTags: string[];
  completedByDiscipline: { discipline: string; tags: string[] }[];
  lines: number; linesPreserved: number; highlights: { id: number; text: string }[];
}
interface Activity { id: number; name: string; status: "Planned" | "Ongoing" | "Hold" | "Completed"; people: number; comment: string | null; isHighlight: boolean }
interface DailyCardData { date: string; unit: DailyUnit; activities: Activity[]; activitiesError: string | null }

const CSS = `
.kc { --ink:#1e1b4b; --violet:#6d28d9; --violet-soft:#ede9fe; --lilac:#c4b5fd; --surface:#f6f5fa; --panel:#ffffff; --line:#e3e0ee;
  --text:#1c1a33; --muted:#5e5a78; --done:#15803d; --done-soft:#dcfce7; --ongoing:#b45309; --ongoing-soft:#fef3c7; --hold:#b91c1c;
  --hold-soft:#fee2e2; --na:#64748b; --na-soft:#eef0f4;
  --sans:"Barlow", ui-sans-serif, system-ui, sans-serif; --display:"Barlow Semi Condensed", "Barlow", ui-sans-serif, sans-serif;
  background:var(--surface); color:var(--text); font-family:var(--sans); font-size:15px; line-height:1.45; padding:16px; }
.kc * { box-sizing:border-box; }
.kc h1, .kc h3, .kc h4 { font-family:var(--display); color:var(--ink); margin:0; }
.kc .num { font-variant-numeric:tabular-nums; }
.kc .head { background:var(--ink); color:#fff; padding:14px 18px; border-radius:10px 10px 0 0; display:flex; justify-content:space-between; align-items:end; gap:12px; }
.kc .head h1 { color:#fff; font-size:24px; }
.kc .eyebrow { color:var(--lilac); font-size:12px; letter-spacing:.08em; text-transform:uppercase; font-weight:600; }
.kc .meta { color:var(--lilac); font-size:13px; text-align:right; }
.kc .meta b { color:#fff; font-weight:600; }
.kc .unit { background:var(--panel); border:1px solid var(--line); border-top:0; border-radius:0 0 10px 10px; padding:16px 18px; display:grid; gap:14px; }
.kc .u-head { display:grid; grid-template-columns:1fr auto; gap:4px 12px; align-items:start; }
.kc .u-head h3 { font-size:24px; }
.kc .stage { display:inline-block; margin-top:4px; font-size:12px; font-weight:600; padding:2px 8px; border-radius:99px; }
.kc .stage.go { background:var(--done-soft); color:var(--done); }
.kc .stage.prep { background:var(--violet-soft); color:var(--violet); }
.kc .pct { text-align:right; }
.kc .pct .big { font-family:var(--display); font-size:40px; font-weight:700; line-height:1; color:var(--ink); }
.kc .pct .sub { font-size:12.5px; color:var(--muted); }
.kc .up { color:var(--done); font-weight:600; }
.kc .bar { grid-column:1 / -1; height:10px; border-radius:99px; background:var(--na-soft); display:flex; overflow:hidden; }
.kc .bar span { display:block; height:100%; }
.kc .bar .p { background:var(--violet); } .kc .bar .y { background:var(--done); } .kc .bar .h { background:var(--hold); opacity:.75; }
.kc .legend { grid-column:1 / -1; display:flex; flex-wrap:wrap; gap:4px 14px; font-size:12.5px; color:var(--muted); }
.kc .legend i { display:inline-block; width:9px; height:9px; border-radius:2px; margin-right:5px; vertical-align:-1px; }
.kc .sec h4 { margin:0 0 6px; font-size:13px; font-weight:600; letter-spacing:.07em; text-transform:uppercase; color:var(--muted); }
.kc table { width:100%; border-collapse:collapse; font-size:14px; }
.kc th { font-size:12.5px; color:var(--muted); font-weight:600; text-align:right; padding:4px 8px; border-bottom:1px solid var(--line); white-space:nowrap; }
.kc th:first-child, .kc td:first-child { text-align:left; padding-left:0; }
.kc td { padding:6px 8px; text-align:right; border-bottom:1px solid var(--line); font-variant-numeric:tabular-nums; white-space:nowrap; }
.kc tr:last-child td { border-bottom:0; }
.kc td.disc { font-weight:600; color:var(--ink); }
.kc .mini { display:inline-flex; align-items:center; gap:6px; justify-content:flex-end; }
.kc .mini b { display:block; width:48px; height:6px; border-radius:99px; background:var(--na-soft); overflow:hidden; }
.kc .mini b i { display:block; height:100%; background:var(--violet); }
.kc .mini span { width:44px; text-align:right; }
.kc .dim { color:var(--muted); }
.kc .piping { font-size:14px; padding:8px 10px; border-radius:6px; background:var(--surface); }
.kc .piping b { font-family:var(--display); color:var(--ink); }
.kc .chips { display:flex; flex-wrap:wrap; gap:6px; margin:0; padding:0; list-style:none; }
.kc .chip { font-size:13px; font-weight:600; padding:3px 8px; border-radius:5px; background:var(--done-soft); color:var(--done); }
.kc .chip.more { background:var(--na-soft); color:var(--muted); }
.kc .cgroups { display:grid; gap:8px; }
.kc .cgroup { display:flex; flex-wrap:wrap; align-items:center; gap:6px 10px; }
.kc .cgroup .cname { min-width:150px; font-size:13px; font-weight:600; color:var(--ink); }
.kc .cgroup .cname small { font-weight:500; color:var(--muted); margin-left:4px; }
.kc .empty { font-size:14px; color:var(--muted); margin:0; }
.kc .warn { font-size:13px; color:var(--hold); margin:0; }
.kc .hl { margin:0; padding:0; list-style:none; display:grid; gap:6px; }
.kc .hl li { position:relative; padding-left:18px; font-size:14.5px; }
.kc .hl li::before { content:""; position:absolute; left:2px; top:.5em; width:8px; height:8px; border-radius:2px; background:var(--violet); transform:rotate(45deg); }
.kc .act { display:grid; grid-template-columns:minmax(0, 1fr) auto auto; gap:2px 10px; align-items:center; padding:8px 0; border-bottom:1px solid var(--line); }
.kc .act:last-child { border-bottom:0; }
.kc .act .n { font-weight:600; color:var(--ink); }
.kc .act .c { grid-column:1 / -1; font-size:13.5px; color:var(--muted); }
.kc .act .c.star { color:var(--text); }
.kc .act .c.star b { color:var(--violet); font-weight:600; }
.kc .pill { font-size:12px; font-weight:600; padding:2px 8px; border-radius:99px; white-space:nowrap; }
.kc .pill.Ongoing { background:var(--ongoing-soft); color:var(--ongoing); }
.kc .pill.Completed { background:var(--done-soft); color:var(--done); }
.kc .pill.Hold { background:var(--hold-soft); color:var(--hold); }
.kc .pill.Planned { background:var(--na-soft); color:var(--na); }
.kc .ppl { font-family:var(--display); font-weight:700; font-size:18px; color:var(--ink); text-align:right; min-width:72px; }
.kc .ppl small { font-family:var(--sans); font-weight:500; font-size:12px; color:var(--muted); }
`;

const LONG = new Intl.DateTimeFormat("en-GB", { weekday: "long", day: "numeric", month: "short", year: "numeric", timeZone: "UTC" });
const SHORT = new Intl.DateTimeFormat("en-GB", { day: "numeric", month: "short", timeZone: "UTC" });
const day = (iso: string) => new Date(`${iso.slice(0, 10)}T00:00:00Z`);
const f1 = (n: number) => (Math.round(n * 10) / 10).toFixed(n >= 10 || n === 0 ? 0 : 1);
/** Tags shown per discipline in "Completed on" (same as the EPCS daily report); the rest as "+N more". */
const TAGS_PER_GROUP = 4;

export function DailyCard({ c, showPiping }: { c: DailyCardData; showPiping: boolean }) {
  const u = c.unit;
  const d = SHORT.format(day(c.date));
  const delta = u.preservedPct - u.preservedPctDayBefore;
  const completed = u.completedTags;
  const w = (n: number) => `${u.applicable > 0 ? Math.max(0, Math.min(100, (100 * n) / u.applicable)) : 0}%`;
  return (
    <div className="kc">
      <style>{CSS}</style>
      <header className="head">
        <div><div className="eyebrow">{u.plantName}</div><h1>Daily Card</h1></div>
        <div className="meta">Work done on <b>{LONG.format(day(c.date))}</b><br />Data up to 24:00 UAE</div>
      </header>
      <article className="unit">
        <div className="u-head">
          <div>
            <h3>{u.unitName}</h3>
            {u.preserved > 0 ? <span className="stage go">Preservation in progress</span> : <span className="stage prep">Preparing for preservation</span>}
          </div>
          <div className="pct">
            <div className="big num">{f1(u.preservedPct)}%</div>
            <div className="sub num">{u.preserved} of {u.applicable} preserved · {delta > 0.05 ? <span className="up">▲ {f1(delta)} pts on {d}</span> : <span>No change on {d}</span>}</div>
          </div>
          <div className="bar"><span className="p" style={{ width: w(u.preserved - completed.length) }} /><span className="y" style={{ width: w(completed.length) }} /><span className="h" style={{ width: w(u.hold) }} /></div>
          <div className="legend">
            <span><i style={{ background: "var(--violet)" }} />Preserved</span><span><i style={{ background: "var(--done)" }} />Completed {d}</span>
            <span><i style={{ background: "var(--hold)", opacity: 0.75 }} />On hold</span><span><i style={{ background: "var(--na-soft)" }} />Remaining</span>
          </div>
        </div>
        <div className="sec">
          <h4>Status by discipline</h4>
          <table>
            <thead><tr><th>Discipline</th><th>Tags</th><th>Preserved</th><th>On hold</th><th>% preserved</th></tr></thead>
            <tbody>
              {u.disciplines.map((r) => {
                const allNa = r.notApplicable === r.tags;
                const pct = allNa ? null : (100 * r.preserved) / (r.tags - r.notApplicable);
                const dash = <span className="dim">—</span>;
                return (
                  <tr key={r.discipline}>
                    <td className="disc">{r.discipline}</td><td>{r.tags}</td><td>{allNa ? dash : r.preserved}</td><td>{allNa ? dash : r.hold}</td>
                    <td>{pct === null ? <span className="dim">Not applicable</span> : <span className="mini"><b><i style={{ width: `${Math.min(100, pct)}%` }} /></b><span>{f1(pct)}%</span></span>}</td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
        {showPiping && u.lines > 0 && <div className="piping"><b>Piping</b> {u.linesPreserved} of {u.lines} lines preserved · {u.lines - u.linesPreserved} pending</div>}
        <div className="sec">
          <h4>Completed on {d}</h4>
          {completed.length === 0 ? <p className="empty">No tags completed on {d}.</p> : (
            <div className="cgroups">
              {u.completedByDiscipline.map((g) => (
                <div key={g.discipline} className="cgroup">
                  <span className="cname">{g.discipline}<small>({g.tags.length})</small></span>
                  <ul className="chips">
                    {g.tags.slice(0, TAGS_PER_GROUP).map((t) => <li key={t} className="chip">{t}</li>)}
                    {g.tags.length > TAGS_PER_GROUP && <li className="chip more">+{g.tags.length - TAGS_PER_GROUP} more</li>}
                  </ul>
                </div>
              ))}
            </div>
          )}
        </div>
        {u.highlights.length > 0 && <div className="sec"><h4>Highlights</h4><ul className="hl">{u.highlights.map((h) => <li key={h.id}>{h.text}</li>)}</ul></div>}
        <div className="sec">
          <h4>Activities</h4>
          {c.activitiesError ? <p className="warn">{c.activitiesError}</p> : c.activities.length === 0 ? <p className="empty">No activity recorded.</p> : (
            <div>
              {c.activities.map((a) => (
                <div key={a.id} className="act">
                  <span className="n">{a.name}</span>
                  <span className={`pill ${a.status}`}>{a.status}</span>
                  <span className="ppl">{a.people}<small> people</small></span>
                  {a.comment && <span className={`c${a.isHighlight ? " star" : ""}`}>{a.isHighlight && <b>Highlight · </b>}{a.comment}</span>}
                </div>
              ))}
            </div>
          )}
        </div>
      </article>
    </div>
  );
}

const CARD_WIDTH_PX = 760;
const PAGE = { w: 210, h: 297, margin: 12 }; // millimetres, A4 portrait

/** Builds the one-unit daily card PDF for a site day and saves it. Returns the file name. */
export async function exportDailyCardPdf(unitId: number, date: string, showPiping: boolean): Promise<string> {
  const card = await api<DailyCardData>(`/api/units/${unitId}/daily-card?date=${date}`);
  const host = document.createElement("div");
  Object.assign(host.style, { position: "fixed", left: "-20000px", top: "0", width: `${CARD_WIDTH_PX}px` });
  document.body.append(host);
  const root = createRoot(host);
  try {
    flushSync(() => root.render(<DailyCard c={card} showPiping={showPiping} />));
    await document.fonts.ready;
    await new Promise((done) => requestAnimationFrame(() => requestAnimationFrame(done)));
    const el = host.firstElementChild as HTMLElement;
    const canvas = await toCanvas(el, { pixelRatio: 2, backgroundColor: "#f6f5fa", fontEmbedCSS: await getFontEmbedCSS(host), cacheBust: false });
    const pdf = new jsPDF({ orientation: "portrait", unit: "mm", format: "a4", compress: true });
    let w = PAGE.w - 2 * PAGE.margin;
    let h = (canvas.height / canvas.width) * w;
    const room = PAGE.h - 2 * PAGE.margin;
    if (h > room) { w = (room / h) * w; h = room; }
    pdf.setFillColor(0xf6, 0xf5, 0xfa);
    pdf.rect(0, 0, PAGE.w, PAGE.h, "F");
    pdf.addImage(canvas.toDataURL("image/png"), "PNG", (PAGE.w - w) / 2, PAGE.margin, w, h, undefined, "FAST");
    const fileName = `${card.unit.unitName} - Daily card - ${card.date}.pdf`;
    pdf.setProperties({ title: `${card.unit.unitName} – Daily card ${card.date}`, subject: card.unit.plantName });
    pdf.save(fileName);
    return fileName;
  } finally {
    root.unmount();
    host.remove();
  }
}
