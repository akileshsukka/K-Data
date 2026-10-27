import type { ImportOutcome, ImportStatus } from "../../api/types";

const outcomeStyle: Record<ImportOutcome, string> = {
  Applied: "bg-done/10 text-done border-done/30",
  Created: "bg-done/10 text-done border-done/30",
  Derived: "bg-violet-soft text-violet border-violet/30",
  "No change": "bg-surface text-muted border-line",
  Invalid: "bg-hold/10 text-hold border-hold/30",
  Conflict: "bg-hold/10 text-hold border-hold/30",
  Rejected: "bg-hold/10 text-hold border-hold/30",
  Warning: "bg-ongoing/10 text-ongoing border-ongoing/30",
};

export function OutcomeChip({ outcome }: { outcome: ImportOutcome }) {
  return <span className={`inline-block rounded border px-1.5 py-0.5 text-sm font-semibold whitespace-nowrap ${outcomeStyle[outcome]}`}>{outcome}</span>;
}

const statusText: Record<ImportStatus, string> = {
  Previewed: "Preview — not applied yet",
  Applied: "Applied",
  AppliedWithIssues: "Applied with issues",
  Rejected: "Rejected",
  Failed: "Failed",
  Cancelled: "Cancelled",
};

const statusStyle: Record<ImportStatus, string> = {
  Previewed: "bg-violet-soft text-violet border-violet/30",
  Applied: "bg-done/10 text-done border-done/30",
  AppliedWithIssues: "bg-ongoing/10 text-ongoing border-ongoing/30",
  Rejected: "bg-hold/10 text-hold border-hold/30",
  Failed: "bg-hold/10 text-hold border-hold/30",
  Cancelled: "bg-surface text-muted border-line",
};

export function StatusChip({ status }: { status: ImportStatus }) {
  return <span className={`inline-block rounded border px-2 py-0.5 text-sm font-semibold whitespace-nowrap ${statusStyle[status]}`}>{statusText[status]}</span>;
}

export const isIssue = (o: ImportOutcome) => o === "Invalid" || o === "Conflict" || o === "Rejected" || o === "Warning";
