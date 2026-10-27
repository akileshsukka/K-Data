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
