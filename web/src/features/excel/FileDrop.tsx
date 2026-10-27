import { useRef, useState } from "react";

/** Drop zone + file picker for one .xlsx file. */
export function FileDrop({ onFile, busy, label, hint }: { onFile: (file: File) => void; busy?: boolean; label: string; hint?: string }) {
  const input = useRef<HTMLInputElement>(null);
  const [over, setOver] = useState(false);

  function take(files: FileList | null) {
    const file = files?.[0];
    if (file) onFile(file);
    if (input.current) input.current.value = "";
  }

  return (
    <div
      onDragOver={(e) => { e.preventDefault(); setOver(true); }}
      onDragLeave={() => setOver(false)}
      onDrop={(e) => { e.preventDefault(); setOver(false); if (!busy) take(e.dataTransfer.files); }}
      className={`flex flex-col items-center justify-center gap-2 rounded-lg border-2 border-dashed px-4 py-8 text-center transition-colors ${over ? "border-violet bg-violet-soft" : "border-line bg-surface"}`}
    >
      {busy ? (
        <p className="flex items-center gap-2 font-semibold text-ink">
          <span className="size-4 animate-spin rounded-full border-2 border-violet border-r-transparent" aria-hidden />
          Checking the file…
        </p>
      ) : (
        <>
          <p className="font-semibold text-ink">Drop the .xlsx file here</p>
          <button type="button" onClick={() => input.current?.click()} className="inline-flex min-h-10 items-center rounded-md border border-line bg-panel px-4 font-semibold text-ink hover:border-violet hover:text-violet">
            {label}
          </button>
          {hint && <p className="max-w-md text-sm text-muted">{hint}</p>}
        </>
      )}
      <input ref={input} type="file" accept=".xlsx,application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" className="hidden" onChange={(e) => take(e.target.files)} />
    </div>
  );
}
