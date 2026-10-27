import { useState } from "react";
import { ApiError } from "../../api/client";
import type { AssetProfile, FieldDef, Proposals } from "../../api/types";
import { useSession } from "../../app/session";
import { Button } from "../../components/Button";
import { useToast } from "../../components/Toast";
import { formatDate, formatDateTime } from "../../components/dates";
import { FieldInput } from "./FieldInput";
import { usePatchAsset, type FieldChange } from "./useAsset";

export function displayValue(field: FieldDef, value: string | null | undefined): string {
  if (!value) return "—";
  if (field.key === "LastReadingAtUtc" || field.key === "PreservedAtUtc") return formatDateTime(value);
  if (field.kind === "Date" || field.key.endsWith("Date")) return formatDate(value);
  return value;
}

/** A card of fields. "Edit" turns the fields the user may change into inputs; only changed fields are sent. */
export function FieldSection({
  title,
  fields,
  asset,
  options,
  onProposals,
}: {
  title: string;
  fields: FieldDef[];
  asset: AssetProfile;
  options: Record<string, string[]>;
  onProposals: (p: Proposals) => void;
}) {
  const session = useSession();
  const toast = useToast();
  const patch = usePatchAsset(asset.id);
  const [draft, setDraft] = useState<Record<string, string> | null>(null);
  const editable = fields.filter((f) => f.kind !== "ReadOnly" && session.can(f.permission));
  const canEdit = editable.length > 0 && (asset.isActive || session.can("assets.manage"));

  function startEdit() {
    setDraft(Object.fromEntries(editable.map((f) => [f.key, asset.values[f.key] ?? ""])));
  }

  async function save() {
    if (!draft) return;
    const changes: FieldChange[] = editable
      .filter((f) => (draft[f.key] ?? "").trim() !== (asset.values[f.key] ?? "").trim())
      .map((f) => ({ field: f.key, from: asset.values[f.key] ?? null, to: draft[f.key].trim() || null }));
    if (changes.length === 0) {
      setDraft(null);
      return;
    }
    try {
      const result = await patch.mutateAsync(changes);
      toast(changes.length === 1 ? `${fields.find((f) => f.key === changes[0].field)?.label} saved` : `${changes.length} fields saved`);
      onProposals(result.proposals);
      setDraft(null);
    } catch (err) {
      toast(err instanceof ApiError ? err.message : "Could not save.", "error");
    }
  }

  return (
    <section className="rounded-lg border border-line bg-panel">
      <header className="flex items-center justify-between border-b border-line px-4 py-2.5">
        <h2 className="text-lg font-semibold">{title}</h2>
        {canEdit && !draft && <Button variant="quiet" onClick={startEdit}>Edit</Button>}
        {draft && (
          <div className="flex gap-2">
            <Button variant="secondary" onClick={() => setDraft(null)}>Cancel</Button>
            <Button busy={patch.isPending} onClick={save}>Save</Button>
          </div>
        )}
      </header>
      <dl className="grid gap-x-6 sm:grid-cols-2">
        {fields.map((f) => {
          const editing = draft && editable.includes(f);
          const wide = f.kind === "LongText";
          return (
            <div key={f.key} className={`border-b border-line/70 px-4 py-2.5 last:border-b-0 ${wide ? "sm:col-span-2" : ""}`}>
              <dt className="text-sm text-muted">
                <label htmlFor={editing ? `f-${f.key}` : undefined}>{f.label}</label>
              </dt>
              <dd className="mt-0.5">
                {editing ? (
                  <FieldInput id={`f-${f.key}`} field={f} value={draft[f.key] ?? ""} onChange={(v) => setDraft({ ...draft, [f.key]: v })} options={options} />
                ) : (
                  <span className={`whitespace-pre-line ${asset.values[f.key] ? "text-text" : "text-muted/60"}`}>{displayValue(f, asset.values[f.key])}</span>
                )}
              </dd>
            </div>
          );
        })}
      </dl>
    </section>
  );
}
