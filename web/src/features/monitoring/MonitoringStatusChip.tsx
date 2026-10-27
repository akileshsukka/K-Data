import type { MonitoringStatus } from "../../api/types";

const tone: Record<MonitoringStatus, string> = {
  Overdue: "bg-hold/10 text-hold border-hold/30",
  "No reading since preserved": "bg-hold/10 text-hold border-hold/30",
  OK: "bg-done/10 text-done border-done/30",
  "During preservation": "bg-violet-soft text-violet border-violet/30",
  "Removed from site": "bg-surface text-muted border-line",
};

const label: Record<MonitoringStatus, string> = {
  Overdue: "Overdue",
  "No reading since preserved": "No reading",
  OK: "Up to date",
  "During preservation": "During",
  "Removed from site": "Removed",
};

/** Monitoring status; the words carry the meaning, the colour only helps. */
export function MonitoringStatusChip({ status, lowN2 }: { status: MonitoringStatus; lowN2?: boolean }) {
  return (
    <span className="inline-flex flex-wrap gap-1">
      <span className={`inline-block rounded border px-1.5 py-0.5 text-sm font-semibold whitespace-nowrap ${tone[status]}`}>{label[status]}</span>
      {lowN2 && <span className="inline-block rounded border border-ongoing/30 bg-ongoing/10 px-1.5 py-0.5 text-sm font-semibold whitespace-nowrap text-ongoing">Low N2</span>}
    </span>
  );
}
