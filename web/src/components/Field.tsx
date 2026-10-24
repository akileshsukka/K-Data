import type { InputHTMLAttributes, ReactNode, SelectHTMLAttributes } from "react";

const control =
  "mt-1 block w-full min-h-10 rounded-md border border-line bg-panel px-3 text-[15px] text-text placeholder:text-muted/70 focus:border-violet focus:outline-none aria-[invalid=true]:border-hold";

export function Field({ label, error, hint, children }: { label: string; error?: string; hint?: string; children: ReactNode }) {
  return (
    <label className="block">
      <span className="text-sm font-semibold text-ink">{label}</span>
      {children}
      {error ? <span className="mt-1 block text-sm text-hold">{error}</span> : hint && <span className="mt-1 block text-sm text-muted">{hint}</span>}
    </label>
  );
}

export function TextInput(props: InputHTMLAttributes<HTMLInputElement>) {
  return <input {...props} className={`${control} ${props.className ?? ""}`} />;
}

export function SelectInput(props: SelectHTMLAttributes<HTMLSelectElement>) {
  return <select {...props} className={`${control} ${props.className ?? ""}`} />;
}
