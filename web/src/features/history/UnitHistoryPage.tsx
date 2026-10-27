import { useState } from "react";
import { useParams } from "react-router";
import { useQuery } from "@tanstack/react-query";
import { api } from "../../api/client";
import type { HistoryEntry } from "../../api/types";
import { useUnit } from "../../app/useCatalog";
import { TextInput } from "../../components/Field";
import { PageHeader } from "../../components/PageHeader";
import { useDebounced } from "../../components/useDebounced";
import { HistoryList } from "../asset/HistoryList";

/** Latest changes in the unit, from the app, bulk edits, Excel uploads and imported history. */
export function UnitHistoryPage() {
  const unitId = Number(useParams().unitId);
  const { unit } = useUnit(unitId);
  const [take, setTake] = useState(300);
  const [text, setText] = useState("");
  const filter = useDebounced(text.trim().toLowerCase(), 300);
  const history = useQuery({
    queryKey: ["unit-history", unitId, take],
    queryFn: ({ signal }) => api<HistoryEntry[]>(`/api/units/${unitId}/history?take=${take}`, { signal }),
  });
  const entries = (history.data ?? []).filter(
    (e) => !filter || `${e.assetTag ?? ""} ${e.assetCode ?? ""} ${e.label} ${e.by} ${e.newValue ?? ""}`.toLowerCase().includes(filter),
  );

  return (
    <div>
      <PageHeader title="History" description={`${unit?.name ?? ""} — latest changes, newest first, in Dubai time.`} />
      <div className="mt-5 max-w-sm">
        <TextInput type="search" placeholder="Filter by tag, field, person or value" value={text} onChange={(e) => setText(e.target.value)} aria-label="Filter history" />
      </div>
      <div className="mt-4">
        {history.isPending ? <p className="text-muted">Loading…</p> : <HistoryList entries={entries} showAsset />}
      </div>
      {history.data && history.data.length === take && (
        <button className="mt-4 font-semibold text-violet" onClick={() => setTake(take + 300)}>Show older changes</button>
      )}
    </div>
  );
}
