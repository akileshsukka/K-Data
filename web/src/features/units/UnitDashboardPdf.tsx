import { useState } from "react";
import type { Dashboard } from "../../api/types";
import { Button } from "../../components/Button";
import { useToast } from "../../components/Toast";

/**
 * "Export PDF" of the Unit overview: the same cards, bars and tables on white A4 pages, without the app frame.
 * The PDF code (and its libraries) only loads when the button is pressed.
 */
export function ExportPdfButton({ unitName, plantName, dashboard }: { unitName: string; plantName: string; dashboard: Dashboard }) {
  const [busy, setBusy] = useState(false);
  const toast = useToast();

  async function run() {
    setBusy(true);
    try {
      const { exportUnitPdf } = await import("./unitPdfReport");
      const name = await exportUnitPdf({ unitName, plantName, dashboard });
      toast(`${name} downloaded`);
    } catch (e) {
      toast((e as Error).message || "Could not create the PDF.", "error");
    } finally {
      setBusy(false);
    }
  }

  return <Button variant="secondary" busy={busy} onClick={run}>{busy ? "Preparing PDF…" : "Export PDF"}</Button>;
}
