import { useState, type FormEvent } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { api } from "../../api/client";
import type { AssetType, CustomField, CustomFieldType } from "../../api/types";
import { useCustomFields } from "../../app/useCatalog";
import { Button } from "../../components/Button";
import { Dialog } from "../../components/Dialog";
import { Field, SelectInput, TextInput } from "../../components/Field";
import { PageHeader } from "../../components/PageHeader";
import { useToast } from "../../components/Toast";
import { AdminTabs } from "./AdminTabs";

const registers: { type: AssetType; label: string }[] = [
  { type: "ElectricalInstrument", label: "E&I Register" },
  { type: "Equipment", label: "Equipment Register" },
  { type: "Pipeline", label: "Pipeline Register" },
];

const kinds: { value: CustomFieldType; label: string }[] = [
  { value: "Text", label: "Text" },
  { value: "Number", label: "Number" },
  { value: "Date", label: "Date" },
  { value: "List", label: "List of options" },
  { value: "YesNo", label: "Yes / No" },
];

/** Fields administrators add to a register without code changes. The E&I register is built from these. */
export function CustomFieldsPage() {
  const [type, setType] = useState<AssetType>("ElectricalInstrument");
  const fields = useCustomFields(undefined, true);
  const [editing, setEditing] = useState<CustomField | "new" | null>(null);
  const list = (fields.data ?? []).filter((f) => f.assetType === type);

  return (
    <div>
      <AdminTabs />
      <PageHeader
        title="Custom fields"
        description="Extra fields of each register: shown on the asset page (grouped by section), optionally as register columns, and loaded from client lists by their name. Fields are deactivated, never deleted."
        actions={<Button onClick={() => setEditing("new")}>New field</Button>}
      />
      <div className="mt-5 flex flex-wrap gap-2" role="tablist">
        {registers.map((r) => (
          <button key={r.type} type="button" role="tab" aria-selected={type === r.type} onClick={() => setType(r.type)}
            className={`min-h-9 rounded-full border px-3 text-sm font-semibold ${type === r.type ? "border-violet bg-violet text-white" : "border-line bg-panel text-ink hover:border-violet"}`}>
            {r.label} <span className="tabular-nums opacity-80">{(fields.data ?? []).filter((f) => f.assetType === r.type).length}</span>
          </button>
        ))}
      </div>

      {fields.isPending ? <p className="mt-6 text-muted">Loading…</p> : list.length === 0 ? (
        <p className="mt-6 max-w-2xl text-muted">No custom fields in this register yet. Add the data the client tracks (for E&I, e.g. instrument type, loop number, range, calibration date).</p>
      ) : (
        <div className="mt-4 overflow-x-auto rounded-lg border border-line bg-panel">
          <table className="w-full border-separate border-spacing-0 text-left text-[15px]">
            <thead className="font-display text-muted">
              <tr>{["Field", "Kind", "Section", "Order", "In register", "Assets with a value", ""].map((h) => <th key={h} className="border-b border-line px-3 py-2.5 font-semibold">{h}</th>)}</tr>
            </thead>
            <tbody>
              {list.map((f) => (
                <tr key={f.id} className={f.isActive ? "" : "text-muted"}>
                  <td className="border-b border-line px-3 py-2"><span className="font-semibold text-ink">{f.label}</span>{!f.isActive && <span className="ml-2 text-sm">(inactive)</span>}</td>
                  <td className="border-b border-line px-3 py-2 text-sm">{kinds.find((k) => k.value === f.dataType)?.label}{f.dataType === "List" && <span className="block text-muted">{f.options.join(", ")}</span>}</td>
                  <td className="border-b border-line px-3 py-2 text-sm">{f.section ?? "—"}</td>
                  <td className="border-b border-line px-3 py-2 tabular-nums">{f.sortOrder}</td>
                  <td className="border-b border-line px-3 py-2">{f.showInRegister ? "Yes" : "No"}</td>
                  <td className="border-b border-line px-3 py-2 tabular-nums">{f.values}</td>
                  <td className="border-b border-line px-3 py-2"><Button variant="quiet" onClick={() => setEditing(f)}>Edit</Button></td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      {editing && <FieldDialog key={editing === "new" ? "new" : editing.id} field={editing === "new" ? undefined : editing} type={type} onClose={() => setEditing(null)} />}
    </div>
  );
}

function FieldDialog({ field, type, onClose }: { field?: CustomField; type: AssetType; onClose: () => void }) {
  const toast = useToast();
  const client = useQueryClient();
  const [f, setF] = useState({
    label: field?.label ?? "", dataType: field?.dataType ?? ("Text" as CustomFieldType), options: field?.options.join("\n") ?? "",
    section: field?.section ?? "", sortOrder: String(field?.sortOrder ?? 10), showInRegister: field?.showInRegister ?? false, isActive: field?.isActive ?? true,
  });
  const save = useMutation({
    mutationFn: () => api<CustomField>(field ? `/api/custom-fields/${field.id}` : "/api/custom-fields", {
      method: field ? "PUT" : "POST",
      body: {
        assetType: field?.assetType ?? type, label: f.label, dataType: f.dataType, options: f.options.split("\n").map((o) => o.trim()).filter(Boolean),
        section: f.section.trim() || null, sortOrder: Number(f.sortOrder) || 0, showInRegister: f.showInRegister, isActive: f.isActive,
      },
    }),
    onSuccess: (r) => {
      client.invalidateQueries({ queryKey: ["catalog", "custom-fields"] });
      client.invalidateQueries({ queryKey: ["register"] });
      client.invalidateQueries({ queryKey: ["custom-values"] });
      toast(`${r.label} saved`);
      onClose();
    },
    onError: (e) => toast(e.message, "error"),
  });
  const locked = !!field && field.values > 0;
  function submit(e: FormEvent) { e.preventDefault(); save.mutate(); }
  return (
    <Dialog open title={field ? `Edit ${field.label}` : `New field — ${registers.find((r) => r.type === type)?.label}`} onClose={onClose}>
      <form onSubmit={submit} className="space-y-4">
        <Field label="Name" hint="Also the column header K Data looks for in client lists.">
          <TextInput required maxLength={128} value={f.label} onChange={(e) => setF({ ...f, label: e.target.value })} />
        </Field>
        <Field label="Kind" hint={locked ? "This field already has values: its kind cannot change." : undefined}>
          <SelectInput value={f.dataType} disabled={locked} onChange={(e) => setF({ ...f, dataType: e.target.value as CustomFieldType })}>
            {kinds.map((k) => <option key={k.value} value={k.value}>{k.label}</option>)}
          </SelectInput>
        </Field>
        {f.dataType === "List" && (
          <Field label="Options" hint="One per line. Removing an option deactivates it; values already saved stay.">
            <textarea required rows={5} value={f.options} onChange={(e) => setF({ ...f, options: e.target.value })}
              className="mt-1 w-full rounded-md border border-line bg-panel p-2 text-[15px] focus:border-violet focus:outline-none" />
          </Field>
        )}
        <div className="grid gap-4 sm:grid-cols-[1fr_7rem]">
          <Field label="Section" hint="Groups fields on the asset page, e.g. Instrument, Calibration.">
            <TextInput maxLength={64} value={f.section} onChange={(e) => setF({ ...f, section: e.target.value })} />
          </Field>
          <Field label="Order">
            <TextInput inputMode="numeric" value={f.sortOrder} onChange={(e) => setF({ ...f, sortOrder: e.target.value })} />
          </Field>
        </div>
        <label className="flex items-center gap-2">
          <input type="checkbox" className="size-4 accent-violet" checked={f.showInRegister} onChange={(e) => setF({ ...f, showInRegister: e.target.checked })} />
          Show as a column of the register
        </label>
        {field && (
          <label className="flex items-center gap-2">
            <input type="checkbox" className="size-4 accent-violet" checked={f.isActive} onChange={(e) => setF({ ...f, isActive: e.target.checked })} />
            Active (inactive fields are hidden; their values and history are kept)
          </label>
        )}
        <div className="flex justify-end gap-2">
          <Button type="button" variant="secondary" onClick={onClose}>Cancel</Button>
          <Button type="submit" busy={save.isPending}>{field ? "Save" : "Create"}</Button>
        </div>
      </form>
    </Dialog>
  );
}
