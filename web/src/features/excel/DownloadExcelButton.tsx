import { useState } from "react";
import { useQueryClient } from "@tanstack/react-query";
import { download } from "../../api/client";
import { Button } from "../../components/Button";
import { useToast } from "../../components/Toast";

/** Builds the unit Excel on the server (a few seconds) and saves it in the browser's downloads. */
export function DownloadExcelButton({ unitId, variant = "primary", label = "Download Excel" }: { unitId: number; variant?: "primary" | "secondary"; label?: string }) {
  const [busy, setBusy] = useState(false);
  const toast = useToast();
  const queryClient = useQueryClient();

  async function run() {
    setBusy(true);
    try {
      const name = await download(`/api/units/${unitId}/excel/export`);
      toast(`${name} downloaded`);
      queryClient.invalidateQueries({ queryKey: ["excel", unitId] });
    } catch (e) {
      toast((e as Error).message, "error");
    } finally {
      setBusy(false);
    }
  }

  return <Button variant={variant} busy={busy} onClick={run}>{busy ? "Preparing file…" : label}</Button>;
}
