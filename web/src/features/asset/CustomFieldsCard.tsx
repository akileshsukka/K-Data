import { useState, type FormEvent } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api, ApiError } from "../../api/client";
import type { AssetProfile, CustomValue } from "../../api/types";
import { useSession } from "../../app/session";
import { Button } from "../../components/Button";
import { SelectInput, TextInput } from "../../components/Field";
import { useToast } from "../../components/Toast";
import { formatDate } from "../../components/dates";

/** The custom fields of the asset's register, one card per section, edited section by section. */
export function CustomFieldsCards({ asset }: { asset: AssetProfile }) {
  const values = useQuery({ queryKey: ["custom-values", asset.id], queryFn: ({ signal }) => api<CustomValue[]>(`/api/assets/${asset.id}/custom-fields`, { signal }) });
  const list = values.data ?? [];
  if (list.length === 0) return null;
  const sections = [...new Set(list.map((v) => v.section ?? "Additional fields"))];
  return <>{sections.map((s) => <SectionCard key={s} title={s} asset={asset} fields={list.filter((v) => (v.section ?? "Additional fields") === s)} />)}</>;
}

function SectionCard({ title, asset, fields }: { title: string; asset: AssetProfile; fields: CustomValue[] }) {
  const session = useSession();
  const toast = useToast();
  const client = useQueryClient();
  const [editing, setEditing] = useState(false);
  const [draft, setDraft] = useState<Record<string, string>>({});
  const canEdit = session.can("assets.edit") && asset.isActive;
  const save = useMutation({
    mutationFn: () => api<CustomValue[]>(`/api/assets/${asset.id}/custom-fields`, {
      method: "PUT",
      body: { changes: fields.filter((f) => (draft[f.key] ?? "") !== (f.value ?? "")).map((f) => ({ key: f.key, from: f.value, to: draft[f.key] ?? "" })) },
    }),
    onSuccess: (r) => {
      client.setQueryData(["custom-values", asset.id], r);
      client.invalidateQueries({ queryKey: ["register"] });
      client.invalidateQueries({ queryKey: ["asset", asset.id, "history"] });
      setEditing(false);
      toast(`${title} saved`);
    },
    onError: (e) => toast(e instanceof ApiError ? e.message : "Could not save.", "error"),
  });
  function start() { setDraft(Object.fromEntries(fields.map((f) => [f.key, f.value ?? ""]))); setEditing(true); }
  function submit(e: FormEvent) { e.preventDefault(); save.mutate(); }
  const show = (f: CustomValue) => f.value == null ? <span className="text-muted">—</span> : f.dataType === "Date" ? formatDate(f.value) : f.value;

  return (
    <section className="rounded-lg border border-line bg-panel">
      <div className="flex items-center justify-between border-b border-line px-4 py-3">
        <h2 className="text-lg font-semibold">{title}</h2>
        {canEdit && !editing && <Button variant="quiet" onClick={start}>Edit</Button>}
      </div>
      {editing ? (
        <form onSubmit={submit} className="grid gap-x-4 gap-y-3 p-4 sm:grid-cols-2">
          {fields.map((f) => (
            <label key={f.key} className="block min-w-0">
              <span className="text-sm text-muted">{f.label}</span>
              {f.dataType === "List" || f.dataType === "YesNo" ? (
                <SelectInput value={draft[f.key] ?? ""} onChange={(e) => setDraft({ ...draft, [f.key]: e.target.value })} className="mt-1">
                  <option value="">—</option>
                  {(f.dataType === "YesNo" ? ["Yes", "No"] : f.options).map((o) => <option key={o}>{o}</option>)}
                  {f.value && f.dataType === "List" && !f.options.includes(f.value) && <option>{f.value}</option>}
                </SelectInput>
              ) : (
                <TextInput type={f.dataType === "Date" ? "date" : "text"} inputMode={f.dataType === "Number" ? "decimal" : undefined} maxLength={500}
                  value={draft[f.key] ?? ""} onChange={(e) => setDraft({ ...draft, [f.key]: e.target.value })} className="mt-1" />
              )}
            </label>
          ))}
          <div className="flex justify-end gap-2 sm:col-span-2">
            <Button type="button" variant="secondary" onClick={() => setEditing(false)}>Cancel</Button>
            <Button type="submit" busy={save.isPending}>Save</Button>
          </div>
        </form>
      ) : (
        <dl className="grid sm:grid-cols-2">
          {fields.map((f) => (
            <div key={f.key} className="border-b border-line px-4 py-2.5 last:border-0">
              <dt className="text-sm text-muted">{f.label}</dt>
              <dd className="break-words text-ink">{show(f)}</dd>
            </div>
          ))}
        </dl>
      )}
    </section>
  );
}
