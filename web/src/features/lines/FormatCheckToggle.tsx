/**
 * Line number format check. On by default and turned back on after every load, so a non-standard number only gets in
 * when someone with lines.nonStandard turns it off on purpose for that load.
 */
export function FormatCheckToggle({ checked, onChange }: { checked: boolean; onChange: (checked: boolean) => void }) {
  return (
    <div className={`rounded-md border px-3 py-2.5 ${checked ? "border-line bg-surface" : "border-ongoing/50 bg-ongoing/5"}`}>
      <label className="flex items-start gap-2 font-semibold text-ink">
        <input type="checkbox" className="mt-1 size-4 accent-violet" checked={checked} onChange={(e) => onChange(e.target.checked)} />
        <span>
          Check line number format
          <span className="block text-sm font-normal text-muted">
            {checked
              ? 'Only numbers like 39-0218-LS-16"-61055-P are accepted.'
              : "Off for this load only: numbers out of the format are loaded as they are and marked as non-standard. Only what is certain is read (size by its inch mark, spec only if it is in the pipe spec list); the rest stays empty. Duplicates are still checked."}
          </span>
        </span>
      </label>
    </div>
  );
}

/** Internal badge of a line whose number is not standard (only roles with lines.nonStandard receive it). */
export function NonStandardBadge({ note, className = "" }: { note?: string | null; className?: string }) {
  return (
    <span title={note ?? undefined} className={`inline-block rounded border border-ongoing/40 bg-ongoing/10 px-1.5 py-0.5 text-xs font-semibold whitespace-nowrap text-ongoing ${className}`}>
      Non-standard no.
    </span>
  );
}
