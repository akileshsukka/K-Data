import { useEffect, useId, useRef, useState } from "react";

export interface MultiOption {
  value: string;
  /** Text of the option in the list (the value when not given). */
  label?: string;
}

/**
 * A filter where several options can be ticked (one OR the other); nothing ticked = any. Looks like the selects next to it.
 * The button says what is ticked: "Any cleaning", "Cleaning: TBC", "Cleaning: TBC, Hydromilling" or "Cleaning: 3 selected".
 * A ticked value no longer among the options (an old link) stays in the list, so it can be unticked.
 */
export function MultiSelect({ value, onChange, options, anyLabel, prefix, label, title, className = "" }: {
  value: string[];
  onChange: (value: string[]) => void;
  options: MultiOption[];
  /** Button text with nothing ticked, e.g. "Any cleaning". */
  anyLabel: string;
  /** Before the ticked options on the button, e.g. "Cleaning". */
  prefix: string;
  /** Accessible name of the filter. */
  label: string;
  title?: string;
  className?: string;
}) {
  const [open, setOpen] = useState(false);
  const root = useRef<HTMLDivElement>(null);
  const button = useRef<HTMLButtonElement>(null);
  const listId = useId();

  useEffect(() => {
    if (!open) return;
    const outside = (e: MouseEvent) => { if (!root.current?.contains(e.target as Node)) setOpen(false); };
    document.addEventListener("mousedown", outside);
    return () => document.removeEventListener("mousedown", outside);
  }, [open]);

  const all: MultiOption[] = [...options, ...value.filter((v) => !options.some((o) => o.value === v)).map((v) => ({ value: v }))];
  const text = (v: string) => all.find((o) => o.value === v)?.label ?? v;
  const picked = all.filter((o) => value.includes(o.value)).map((o) => o.label ?? o.value);
  const shown = picked.join(", ");
  const summary = picked.length === 0 ? anyLabel : `${prefix}: ${picked.length > 2 && shown.length > 28 ? `${picked.length} selected` : shown}`;

  const toggle = (v: string) => onChange(value.includes(v) ? value.filter((x) => x !== v) : [...value, v]);

  return (
    <div ref={root} className={`relative ${className}`}
      onKeyDown={(e) => { if (e.key === "Escape" && open) { e.stopPropagation(); setOpen(false); button.current?.focus(); } }}
      onBlur={(e) => { if (!root.current?.contains(e.relatedTarget as Node | null)) setOpen(false); }}>
      <button ref={button} type="button" aria-expanded={open} aria-controls={listId} aria-label={`${label}: ${picked.length === 0 ? "any" : picked.join(", ")}`}
        title={title ?? (picked.length > 0 ? `${prefix}: ${picked.map(text).join(" or ")}` : undefined)}
        onClick={() => setOpen(!open)}
        className={`flex min-h-10 w-full items-center justify-between gap-2 rounded-md border bg-panel px-3 text-left text-[15px] focus:border-violet focus:outline-none ${picked.length > 0 ? "border-violet/60 text-ink" : "border-line text-text"}`}>
        <span className="truncate">{summary}</span>
        <span aria-hidden className="shrink-0 text-xs text-muted">▾</span>
      </button>
      {open && (
        <div id={listId} role="group" aria-label={`${label} (tick one or more)`}
          className="absolute left-0 z-30 mt-1 max-h-80 w-full min-w-[14rem] overflow-y-auto rounded-md border border-line bg-panel py-1 shadow-lg">
          {all.length === 0 && <p className="px-3 py-2 text-sm text-muted">No values</p>}
          {all.map((o) => (
            <label key={o.value} className="flex cursor-pointer items-center gap-2 px-3 py-1.5 text-[15px] hover:bg-surface">
              <input type="checkbox" className="size-4 shrink-0 accent-violet" checked={value.includes(o.value)} onChange={() => toggle(o.value)} />
              <span className="min-w-0 break-words">{o.label ?? o.value}</span>
            </label>
          ))}
          {value.length > 0 && (
            <button type="button" onClick={() => onChange([])} className="mt-1 w-full border-t border-line px-3 py-2 text-left text-sm font-semibold text-violet hover:bg-surface">
              Clear ({anyLabel.toLowerCase()})
            </button>
          )}
        </div>
      )}
    </div>
  );
}
