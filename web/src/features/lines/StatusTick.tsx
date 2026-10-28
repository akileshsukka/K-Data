/** A small tick of the Pipeline Register: green done, amber ongoing, red not done, blue not applicable. */
export type Tick = "done" | "ongoing" | "missing" | "na";

const styles: Record<Tick, { cls: string; mark: string; text: string }> = {
  done: { cls: "bg-done text-white", mark: "✓", text: "done" },
  ongoing: { cls: "bg-ongoing text-white", mark: "•", text: "ongoing" },
  missing: { cls: "bg-hold text-white", mark: "✕", text: "not done" },
  na: { cls: "bg-info text-white", mark: "–", text: "not applicable" },
};

export function StatusTick({ tick, label, value }: { tick: Tick; label: string; value: string | null | undefined }) {
  const s = styles[tick];
  const title = `${label}: ${value || s.text}`;
  return (
    <span title={title} aria-label={title} role="img" className={`inline-flex size-5 items-center justify-center rounded-full text-[12px] leading-none font-bold ${s.cls}`}>
      {s.mark}
    </span>
  );
}

/** EM Walkdown: Completed / Ongoing / Not Applicable; anything else (empty) is not done. */
export function walkdownTick(v: string | null | undefined): Tick {
  const k = (v ?? "").trim().toLowerCase();
  return k === "completed" ? "done" : k === "ongoing" ? "ongoing" : k === "not applicable" || k === "n/a" ? "na" : "missing";
}

/** Bulk liquid drained of a line: Completed (the list's "Yes") or not yet. */
export function drainedTick(v: string | null | undefined): Tick {
  const k = (v ?? "").trim().toLowerCase();
  return k === "completed" ? "done" : k === "not applicable" ? "na" : k ? "ongoing" : "missing";
}

/** Inspection Done: Yes or No (empty counts as No). */
export function yesTick(v: string | null | undefined): Tick {
  return (v ?? "").trim().toLowerCase() === "yes" ? "done" : "missing";
}
