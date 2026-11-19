import { useState } from "react";
import { Button } from "../../components/Button";
import { useToast } from "../../components/Toast";

/**
 * "Export PDF" of the Unit overview: the same cards, bars and tables on white A4 pages, without the app frame.
 * The report always holds every discipline of the unit, whichever dashboard is on screen: mechanical equipment,
 * then Electrical & Instrumentation, then pipelines, each section starting on its own page.
 * The PDF code (and its libraries) only loads when the button is pressed.
 */
/** grouped: the view on screen (split items as the client or by equipment); not given = the unit default. */
export function ExportPdfButton({ unitId, unitName, plantName, grouped }: { unitId: number; unitName: string; plantName: string; grouped?: boolean }) {
  const [busy, setBusy] = useState(false);
  const toast = useToast();

  async function run() {
    setBusy(true);
    try {
      const { exportUnitPdf } = await import("./unitPdfReport");
      const name = await exportUnitPdf({ unitId, unitName, plantName, grouped });
      toast(`${name} downloaded`);
    } catch (e) {
      toast((e as Error).message || "Could not create the PDF.", "error");
    } finally {
      setBusy(false);
    }
  }

  return <Button variant="secondary" busy={busy} onClick={run}>{busy ? "Preparing PDF…" : "Export PDF"}</Button>;
}
