/** Gate status as a small coloured label. Colour carries meaning only: done, in progress, on hold, not applicable. */
const tone: Record<string, string> = {
  completed: "bg-done/12 text-done border-done/30",
  ongoing: "bg-ongoing/10 text-ongoing border-ongoing/30",
  hold: "bg-hold/10 text-hold border-hold/30",
  "not applicable": "bg-na/10 text-na border-na/25",
};

export function GateStatus({ value, compact = false }: { value: string | null | undefined; compact?: boolean }) {
  if (!value) return <span className="text-muted/60">—</span>;
  const style = tone[value.toLowerCase()] ?? "bg-surface text-ink border-line";
  const text = compact && value === "Not Applicable" ? "N/A" : value;
  return <span className={`inline-block rounded border px-1.5 py-0.5 text-[13px] leading-tight font-semibold whitespace-nowrap ${style}`}>{text}</span>;
}

/** Current Gate shown as a gate number chip ("G4"), with Removed from Site set apart. */
export function CurrentGateChip({ value }: { value: string }) {
  const removed = value === "Removed from Site";
  const isNew = value === "New";
  return (
    <span
      className={`inline-block rounded-md px-2 py-0.5 font-display text-[15px] font-bold whitespace-nowrap ${
        removed ? "bg-na/15 text-na" : isNew ? "bg-surface text-muted border border-line" : "bg-ink text-white"
      }`}
    >
      {value}
    </span>
  );
}
