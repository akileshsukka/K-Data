import { useState } from "react";
import { useQueryClient } from "@tanstack/react-query";
import { download } from "../../api/client";
import { Button } from "../../components/Button";
import { useToast } from "../../components/Toast";

/** Builds the unit Excel (or the piping line list, or the E7 electrical test report) on the server and saves it in the browser's downloads. */
export function DownloadExcelButton({ unitId, variant = "primary", label = "Download Excel", piping = false, path, body }: { unitId: number; variant?: "primary" | "secondary"; label?: string; piping?: boolean; path?: string; body?: unknown }) {
  const [busy, setBusy] = useState(false);
  const toast = useToast();
  const queryClient = useQueryClient();

  async function run() {
    setBusy(true);
    try {
      const name = await download(path ?? `/api/units/${unitId}/excel/${piping ? "piping/" : ""}export`, body);
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
