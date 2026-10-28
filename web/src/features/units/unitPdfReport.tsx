import type React from "react";
import type { ReactNode } from "react";
import { flushSync } from "react-dom";
import { createRoot } from "react-dom/client";
import { getFontEmbedCSS, toCanvas } from "html-to-image";
import { jsPDF } from "jspdf";
import { api } from "../../api/client";
import type { CategoryRow, Dashboard, PipingSummary } from "../../api/types";
import { PipingCards, PipingServiceTable } from "../lines/PipingSummaryCards";
import { ActivityBar, DisciplineTable, KeyFigures, Legend, categoryDetail } from "./UnitHomePage";

/*
  How the PDF is made: the report below is drawn off screen at a fixed width with the same components as the
  Unit overview, each block (cards, a panel, a slice of a table) is turned into an image, and the blocks are laid
  on A4 pages one under the other. A block never splits across two pages; long tables and category lists are
  cut into several blocks beforehand (by whole rows), each repeating its title and headings.
*/

const REPORT_WIDTH_PX = 1000;
const PAGE = { w: 210, h: 297, margin: 12, bottom: 14, gap: 4 }; // millimetres, A4 portrait
const TABLE_ROWS_PER_BLOCK = 12;

/** A plain white sheet: the K Data violet accents become neutral greys, the status colours stay. */
const NEUTRAL = {
  "--color-violet": "#475569",
  "--color-violet-dark": "#334155",
  "--color-violet-soft": "#f1f5f9",
  "--color-ink": "#111827",
  "--color-surface": "#e5e7eb",
} as React.CSSProperties;
const CATEGORY_WEIGHT_PER_BLOCK = 14;

interface ReportInput {
  unitName: string;
  plantName: string;
  dashboard: Dashboard;
}

/** Builds the PDF of the Unit overview and saves it in the browser's downloads. Returns the file name. */
export async function exportUnitPdf({ unitName, plantName, dashboard }: ReportInput): Promise<string> {
  const piping = dashboard.lineCount > 0 ? await api<PipingSummary>(`/api/units/${dashboard.unitId}/piping-summary`) : null;
  const now = new Date();

  const host = document.createElement("div");
  // Off screen but laid out normally, so every block has its real size.
  Object.assign(host.style, { position: "fixed", left: "-20000px", top: "0", width: `${REPORT_WIDTH_PX}px`, background: "#ffffff" });
  document.body.append(host);
  const root = createRoot(host);
  try {
    flushSync(() => root.render(<Report unitName={unitName} plantName={plantName} d={dashboard} piping={piping} at={now} />));
    await document.fonts.ready;
    await new Promise((r) => requestAnimationFrame(() => requestAnimationFrame(r)));

    const blocks = [...host.querySelectorAll<HTMLElement>("[data-pdf-block]")];
    const fontEmbedCSS = await getFontEmbedCSS(host);
    const images: PdfBlock[] = [];
    for (const block of blocks) {
      images.push({
        canvas: await toCanvas(block, { pixelRatio: 2, backgroundColor: "#ffffff", fontEmbedCSS, cacheBust: false }),
        newPage: block.dataset.pdfNewPage === "true",
      });
    }

    const pdf = layout(images, `${unitName} · ${plantName} · Preservation dashboard · ${formatDate(now)}`);
    const fileName = `${unitName} - Preservation dashboard - ${now.toISOString().slice(0, 10)}.pdf`;
    pdf.setProperties({ title: `${unitName} – Preservation dashboard`, subject: plantName });
    pdf.save(fileName);
    return fileName;
  } finally {
    root.unmount();
    host.remove();
  }
}

interface PdfBlock {
  canvas: HTMLCanvasElement;
  /** Starts a new page (the Pipelines section always begins on its own page). */
  newPage: boolean;
}

/** Lays the block images on A4 pages (never splitting one) and writes the footer (unit, plant, date) on every page. */
function layout(images: PdfBlock[], footer: string) {
  const pdf = new jsPDF({ orientation: "portrait", unit: "mm", format: "a4", compress: true });
  const contentW = PAGE.w - 2 * PAGE.margin;
  const bottom = PAGE.h - PAGE.bottom;
  const fullH = bottom - PAGE.margin;
  let y = PAGE.margin;
  let first = true;

  for (const { canvas: img, newPage } of images) {
    let w = contentW;
    let h = (img.height / img.width) * w;
    if (h > fullH) {
      // Only a block taller than a whole page (very unusual) is scaled down, to keep it whole.
      w = (fullH / h) * w;
      h = fullH;
    }
    if (!first && (newPage || y + h > bottom)) {
      pdf.addPage();
      y = PAGE.margin;
    }
    pdf.addImage(img, "PNG", PAGE.margin + (contentW - w) / 2, y, w, h, undefined, "FAST");
    y += h + PAGE.gap;
    first = false;
  }

  const pages = pdf.getNumberOfPages();
  for (let i = 1; i <= pages; i++) {
    pdf.setPage(i);
    pdf.setFont("helvetica", "normal");
    pdf.setFontSize(8);
    pdf.setTextColor(110, 110, 120);
    pdf.text(footer, PAGE.margin, PAGE.h - 7);
    // Page numbers are off for now, so the Pipelines pages can be removed from the file without a gap in the numbering.
    // pdf.text(`Page ${i} of ${pages}`, PAGE.w - PAGE.margin, PAGE.h - 7, { align: "right" });
  }
  return pdf;
}

function formatDate(d: Date) {
  return d.toLocaleString("en-GB", { day: "2-digit", month: "short", year: "numeric", hour: "2-digit", minute: "2-digit" });
}

/** Even slices of at most `size` rows (25 rows → 13 + 12, not 12 + 12 + 1). */
function chunk<T>(rows: T[], max: number): T[][] {
  const size = Math.ceil(rows.length / Math.max(1, Math.ceil(rows.length / max)));
  const out: T[][] = [];
  for (let i = 0; i < rows.length; i += size) out.push(rows.slice(i, i + size));
  return out.length ? out : [[]];
}

/** Category slices sized by how many lines they take (a category with many tags weighs more). */
function chunkCategories(rows: CategoryRow[]): CategoryRow[][] {
  const out: CategoryRow[][] = [];
  let current: CategoryRow[] = [];
  let weight = 0;
  for (const c of rows) {
    const w = 2 + Math.ceil(c.assets.length / 9);
    if (current.length && weight + w > CATEGORY_WEIGHT_PER_BLOCK) {
      out.push(current);
      current = [];
      weight = 0;
    }
    current.push(c);
    weight += w;
  }
  out.push(current);
  return out;
}

// ---------------------------------------------------------------- the report (white page, no app frame)

function Report({ unitName, plantName, d, piping, at }: { unitName: string; plantName: string; d: Dashboard; piping: PipingSummary | null; at: Date }) {
  const byKeyword = d.categorySource === "Keywords";
  return (
    <div className="bg-white text-text" style={{ width: REPORT_WIDTH_PX, ...NEUTRAL }}>
      <Block>
        <header className="flex items-end justify-between gap-4 border-b border-line pb-4">
          <div>
            <h1 className="text-[2rem] leading-tight font-semibold">{unitName}</h1>
            <p className="mt-1 text-muted">{plantName} · Preservation dashboard</p>
          </div>
          <p className="text-sm text-muted">Generated {formatDate(at)}</p>
        </header>
      </Block>

      {d.totalTags > 0 && (
        <>
          <Block>
            <div className="grid grid-cols-6 gap-3"><KeyFigures d={d} /></div>
          </Block>

          <Block>
            <section className="rounded-lg border border-line bg-white p-5">
              <div className="flex items-baseline justify-between gap-2">
                <h2 className="text-xl font-semibold">Activity progress</h2>
                <Legend />
              </div>
              <ul className="mt-4 space-y-4">
                {d.activities.map((a) => <ActivityBar key={a.label} row={a} />)}
              </ul>
            </section>
          </Block>
        </>
      )}

      {d.totalTags > 0 && (
        <>
          {chunk(d.disciplines, TABLE_ROWS_PER_BLOCK).map((rows, i) => (
            <Block key={`disc${i}`}>
              <Panel title={i === 0 ? "Discipline-wise status" : "Discipline-wise status (continued)"}>
                <DisciplineTable rows={rows} />
              </Panel>
            </Block>
          ))}

          <Categories title="Hold categories" rows={d.hold} total={d.totalHold} byKeyword={byKeyword} />
          <Categories title="Not applicable categories" rows={d.notApplicable} total={d.totalNotApplicable} byKeyword={byKeyword} />

        </>
      )}

      {piping && piping.lines > 0 && (
        <>
          {/* Pipelines always start on a new page, so the section can be reviewed (or left out) on its own. */}
          <Block newPage>
            <h2 className="text-xl font-semibold">Pipelines</h2>
            <div className="mt-3 grid grid-cols-6 gap-3"><PipingCards s={piping} /></div>
          </Block>
          {piping.byService.length > 0 && chunk(piping.byService, TABLE_ROWS_PER_BLOCK).map((rows, i) => (
            <Block key={`svc${i}`}>
              <Panel title={i === 0 ? "Pipelines by service" : "Pipelines by service (continued)"}>
                <PipingServiceTable rows={rows} />
              </Panel>
            </Block>
          ))}
        </>
      )}

    </div>
  );
}

/** One piece that stays whole on a page. The padding is the white space around it in the PDF. */
function Block({ children, newPage = false }: { children: ReactNode; newPage?: boolean }) {
  return <div data-pdf-block data-pdf-new-page={newPage ? "true" : undefined} className="bg-white px-1 py-1">{children}</div>;
}

function Panel({ title, children }: { title: string; children: ReactNode }) {
  return (
    <section className="rounded-lg border border-line bg-white">
      <h2 className="px-5 pt-5 text-xl font-semibold">{title}</h2>
      <div className="mt-3">{children}</div>
    </section>
  );
}

/** Hold or N/A categories with every tag listed (the app shows them on click; the PDF shows them all). */
function Categories({ title, rows, total, byKeyword }: { title: string; rows: CategoryRow[]; total: number; byKeyword: boolean }) {
  if (rows.length === 0) {
    return (
      <Block>
        <Panel title={title}><p className="px-5 pb-5 text-muted">No categories yet.</p></Panel>
      </Block>
    );
  }
  const slices = chunkCategories(rows);
  return (
    <>
      {slices.map((slice, i) => (
        <Block key={i}>
          <Panel title={i === 0 ? title : `${title} (continued)`}>
            <ul>
              {slice.map((c) => (
                <li key={c.id} className="border-t border-line px-5 py-3">
                  <div className="grid grid-cols-[1fr_auto] items-center gap-3">
                    <span>
                      <span className="block font-semibold text-ink">
                        {c.code && <span className="mr-2 text-hold">{c.code}</span>}
                        {c.label}
                      </span>
                      <span className="block text-sm text-muted">{categoryDetail(c, byKeyword)}</span>
                    </span>
                    <span className="font-display text-2xl font-bold text-ink tabular-nums">{c.count}</span>
                  </div>
                  {c.assets.length > 0 && (
                    <ul className="mt-2 flex flex-wrap gap-1.5">
                      {c.assets.map((a) => (
                        <li key={a.id} className="rounded-md border border-line px-2 py-0.5 text-sm font-semibold text-ink">{a.tag ?? a.code}</li>
                      ))}
                    </ul>
                  )}
                </li>
              ))}
              {i === slices.length - 1 && (
                <li className="flex justify-between border-t border-line px-5 py-3 font-semibold">
                  <span>Total</span>
                  <span className="tabular-nums">{total}</span>
                </li>
              )}
            </ul>
          </Panel>
        </Block>
      ))}
    </>
  );
}
