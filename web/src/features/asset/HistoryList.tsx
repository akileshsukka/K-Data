import { Link } from "react-router";
import type { HistoryEntry } from "../../api/types";
import { formatDateTime } from "../../components/dates";
import { gateText } from "../../components/GateStatus";

const sourceLabel: Record<string, string> = {
  App: "App", Bulk: "Bulk edit", Excel: "Excel upload", Migration: "Migration", HistoryImport: "Imported history", Simpro: "Simpro", System: "System", Onboarding: "Initial load",
};

/** Who changed what and when, newest first. `type`: the asset type, so an E&I item reads E1–E8 instead of G1–G8. */
export function HistoryList({ entries, showAsset = false, type }: { entries: HistoryEntry[]; showAsset?: boolean; type?: string }) {
  const electrical = type === "ElectricalInstrument";
  const label = (e: HistoryEntry) => (electrical ? e.label.replace(/^G([1-8]) (Status|Comments)$/, "E$1 $2") : e.label);
  const value = (e: HistoryEntry, v: string | null) => (v !== null && e.field === "CurrentGate" ? gateText(v, type) : v);
  if (entries.length === 0) return <p className="text-muted">No changes recorded yet.</p>;
  return (
    <ol className="divide-y divide-line rounded-lg border border-line bg-panel">
      {entries.map((e, i) => (
        <li key={i} className="grid gap-1 px-4 py-3 sm:grid-cols-[11rem_1fr]">
          <div className="text-sm text-muted">
            {formatDateTime(e.atUtc)}
            <span className="block">{e.by}</span>
          </div>
          <div className="min-w-0">
            {showAsset && e.assetId && (
              <Link to={`/assets/${e.assetId}`} className="font-semibold text-violet">{e.assetTag ?? e.assetCode}</Link>
            )}
            <p>
              <span className="font-semibold text-ink">{label(e)}</span>
              {e.field === "(created)" ? (
                <span className="text-muted"> — asset created</span>
              ) : (
                <>
                  <span className="text-muted">: </span>
                  <span className="text-muted line-through decoration-muted/50">{value(e, e.oldValue) ?? "empty"}</span>
                  <span className="text-muted"> → </span>
                  <span className="whitespace-pre-line">{value(e, e.newValue) ?? "empty"}</span>
                </>
              )}
              {e.source && <span className="ml-2 rounded bg-surface px-1.5 py-0.5 text-xs text-muted">{sourceLabel[e.source] ?? e.source}</span>}
            </p>
          </div>
        </li>
      ))}
    </ol>
  );
}
