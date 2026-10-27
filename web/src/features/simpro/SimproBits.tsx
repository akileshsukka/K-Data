import type { SimproRun } from "../../api/types";

const style: Record<SimproRun["status"], string> = {
  Running: "bg-violet-soft text-violet border-violet/30",
  Previewed: "bg-violet-soft text-violet border-violet/30",
  Applied: "bg-done/10 text-done border-done/30",
  HeldForConflicts: "bg-ongoing/10 text-ongoing border-ongoing/30",
  Failed: "bg-hold/10 text-hold border-hold/30",
  Undone: "bg-surface text-muted border-line",
};

const text: Record<SimproRun["status"], string> = {
  Running: "Running…",
  Previewed: "Preview — nothing written",
  Applied: "Written",
  HeldForConflicts: "Written, some held",
  Failed: "Failed — nothing written",
  Undone: "Undone",
};

export function RunStatus({ run }: { run: SimproRun }) {
  return <span className={`inline-block rounded border px-2 py-0.5 text-sm font-semibold whitespace-nowrap ${style[run.status]}`}>{text[run.status]}</span>;
}

/** Outcome of one field; the label always carries the meaning, the colour only helps. */
export function ItemOutcome({ outcome }: { outcome: string }) {
  const tone =
    outcome === "Written" || outcome === "Created" ? "bg-done/10 text-done border-done/30"
      : outcome === "To write" || outcome === "To create" ? "bg-violet-soft text-violet border-violet/30"
        : outcome === "Conflict" || outcome === "Invalid" || outcome === "Not created" ? "bg-hold/10 text-hold border-hold/30"
          : outcome === "Changed in Simpro" || outcome === "Gate goes back" ? "bg-ongoing/10 text-ongoing border-ongoing/30"
            : "bg-surface text-muted border-line";
  return <span className={`inline-block rounded border px-1.5 py-0.5 text-sm font-semibold whitespace-nowrap ${tone}`}>{outcome}</span>;
}
