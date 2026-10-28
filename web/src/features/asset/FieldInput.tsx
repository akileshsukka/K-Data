import type { FieldDef } from "../../api/types";
import { SelectInput, TextInput } from "../../components/Field";

/** The right control for a field kind: list → select, long text → textarea, date → date picker. */
export function FieldInput({
  field,
  value,
  onChange,
  options,
  id,
}: {
  field: FieldDef;
  value: string;
  onChange: (v: string) => void;
  options: Record<string, string[]>;
  id?: string;
}) {
  if (field.kind === "Choice" && field.multiple) {
    // Several values of the list, stored one per line (piping Current Activity).
    const list = options[field.optionSet ?? ""] ?? [];
    const chosen = value.split("\n").map((v) => v.trim()).filter(Boolean);
    const has = (o: string) => chosen.some((c) => c.toLowerCase() === o.toLowerCase());
    const legacy = chosen.filter((c) => !list.some((o) => o.toLowerCase() === c.toLowerCase()));
    const toggle = (o: string) => onChange((has(o) ? chosen.filter((c) => c.toLowerCase() !== o.toLowerCase()) : [...chosen, o]).join("\n"));
    return (
      <div id={id} role="group" className="grid gap-1 rounded-md border border-line bg-panel px-3 py-2">
        {list.map((o) => (
          <label key={o} className="inline-flex items-center gap-2 text-[15px]">
            <input type="checkbox" className="size-4 accent-violet" checked={has(o)} onChange={() => toggle(o)} />
            {o}
          </label>
        ))}
        {legacy.map((o) => (
          <label key={o} className="inline-flex items-center gap-2 text-[15px] text-muted">
            <input type="checkbox" className="size-4 accent-violet" checked onChange={() => onChange(chosen.filter((c) => c !== o).join("\n"))} />
            {o} (not in list)
          </label>
        ))}
      </div>
    );
  }
  if (field.kind === "Choice") {
    const list = options[field.optionSet ?? ""] ?? [];
    const legacy = value && !list.some((o) => o.toLowerCase() === value.toLowerCase());
    return (
      <SelectInput id={id} value={value} onChange={(e) => onChange(e.target.value)} className="mt-0">
        <option value="">—</option>
        {legacy && <option value={value}>{value} (not in list)</option>}
        {list.map((o) => <option key={o}>{o}</option>)}
      </SelectInput>
    );
  }
  if (field.kind === "LongText") {
    return (
      <textarea
        id={id}
        value={value}
        maxLength={field.maxLength}
        rows={Math.min(8, Math.max(3, value.split("\n").length + 1))}
        onChange={(e) => onChange(e.target.value)}
        className="block w-full rounded-md border border-line bg-panel px-3 py-2 text-[15px] focus:border-violet focus:outline-none"
      />
    );
  }
  if (field.kind === "Date") {
    return <TextInput id={id} type="date" value={value} onChange={(e) => onChange(e.target.value)} className="mt-0" />;
  }
  return <TextInput id={id} value={value} maxLength={field.maxLength || undefined} onChange={(e) => onChange(e.target.value)} className="mt-0" />;
}
