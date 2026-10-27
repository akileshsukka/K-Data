import { useState } from "react";
import { ApiError } from "../../api/client";
import type { AssetProfile, FieldDef, GateDoc, Proposals } from "../../api/types";
import { useSession } from "../../app/session";
import { Button } from "../../components/Button";
import { SelectInput, TextInput } from "../../components/Field";
import { GateStatus } from "../../components/GateStatus";
import { useToast } from "../../components/Toast";
import { formatDate, todayDubai } from "../../components/dates";
import { FieldInput } from "./FieldInput";
import { usePatchAsset, useSetGateDoc } from "./useAsset";

const docLabel: Record<GateDoc["status"], string> = { Pending: "Pending", Received: "Received", NotApplicable: "Not Applicable" };

/**
 * G1–G8 in one place: each gate's status, its details and comments, and next to it whether its
 * formal documentation arrived. A Completed gate always shows its documentation line.
 */
export function GatesTab({ asset, fields, options, onProposals }: {
  asset: AssetProfile;
  fields: FieldDef[];
  options: Record<string, string[]>;
  onProposals: (p: Proposals) => void;
}) {
  return (
    <ol className="space-y-3">
      {Array.from({ length: 8 }, (_, i) => i + 1).map((gate) => (
        <GateRow
          key={gate}
          gate={gate}
          asset={asset}
          fields={fields.filter((f) => f.gate === gate)}
          options={options}
          onProposals={onProposals}
        />
      ))}
    </ol>
  );
}

function GateRow({ gate, asset, fields, options, onProposals }: {
  gate: number;
  asset: AssetProfile;
  fields: FieldDef[];
  options: Record<string, string[]>;
  onProposals: (p: Proposals) => void;
}) {
  const session = useSession();
  const toast = useToast();
  const patch = usePatchAsset(asset.id);
  const statusField = fields.find((f) => f.key === `G${gate}Status`)!;
  const commentField = fields.find((f) => f.key === `G${gate}Comments`)!;
  const details = fields.filter((f) => f !== statusField && f !== commentField);
  const status = asset.values[statusField.key];
  const doc = asset.gateDocuments.find((d) => d.gate === gate);
  const canEdit = session.can(statusField.permission) && (asset.isActive || session.can("assets.manage"));
  const [editing, setEditing] = useState<Record<string, string> | null>(null);

  async function saveStatus(value: string) {
    try {
      const r = await patch.mutateAsync([{ field: statusField.key, from: status ?? null, to: value || null }]);
      toast(`G${gate} set to ${value || "empty"}`);
      onProposals(r.proposals);
    } catch (err) {
      toast(err instanceof ApiError ? err.message : "Could not save.", "error");
    }
  }

  async function saveDetails() {
    if (!editing) return;
    const changes = [commentField, ...details]
      .filter((f) => (editing[f.key] ?? "") !== (asset.values[f.key] ?? ""))
      .map((f) => ({ field: f.key, from: asset.values[f.key] ?? null, to: editing[f.key].trim() || null }));
    try {
      if (changes.length > 0) {
        const r = await patch.mutateAsync(changes);
        onProposals(r.proposals);
        toast(`G${gate} details saved`);
      }
      setEditing(null);
    } catch (err) {
      toast(err instanceof ApiError ? err.message : "Could not save.", "error");
    }
  }

  return (
    <li className="rounded-lg border border-line bg-panel">
      <div className="flex flex-wrap items-center gap-3 px-4 py-3">
        <span className="w-10 font-display text-2xl font-bold text-ink">G{gate}</span>
        <div className="min-w-[11rem]">
          {canEdit ? (
            <SelectInput value={status ?? ""} disabled={patch.isPending} onChange={(e) => saveStatus(e.target.value)} aria-label={`G${gate} status`} className="mt-0">
              <option value="">Not set</option>
              {(options.GateStatus ?? []).map((o) => <option key={o}>{o}</option>)}
            </SelectInput>
          ) : (
            <GateStatus value={status} />
          )}
        </div>
        <DocumentCell asset={asset} gate={gate} doc={doc} completed={status === "Completed"} />
        {canEdit && !editing && (
          <Button variant="quiet" className="ml-auto" onClick={() => setEditing(Object.fromEntries([commentField, ...details].map((f) => [f.key, asset.values[f.key] ?? ""])))}>
            Edit details
          </Button>
        )}
      </div>

      {(editing || details.some((f) => asset.values[f.key]) || asset.values[commentField.key]) && (
        <div className="space-y-3 border-t border-line px-4 py-3">
          {[...details, commentField].map((f) =>
            editing ? (
              <label key={f.key} className="block">
                <span className="text-sm text-muted">{f.label}</span>
                <FieldInput field={f} value={editing[f.key] ?? ""} onChange={(v) => setEditing({ ...editing, [f.key]: v })} options={options} />
              </label>
            ) : asset.values[f.key] ? (
              <div key={f.key}>
                <span className="text-sm text-muted">{f.label}</span>
                <p className="whitespace-pre-line">{asset.values[f.key]}</p>
              </div>
            ) : null,
          )}
          {editing && (
            <div className="flex justify-end gap-2">
              <Button variant="secondary" onClick={() => setEditing(null)}>Cancel</Button>
              <Button busy={patch.isPending} onClick={saveDetails}>Save</Button>
            </div>
          )}
        </div>
      )}
    </li>
  );
}

function DocumentCell({ asset, gate, doc, completed }: { asset: AssetProfile; gate: number; doc?: GateDoc; completed: boolean }) {
  const session = useSession();
  const toast = useToast();
  const save = useSetGateDoc(asset.id);
  const [form, setForm] = useState<{ status: string; receivedOn: string; reference: string } | null>(null);
  if (!doc && !completed) return null;
  const canEdit = session.can("assets.edit");

  async function submit() {
    if (!form) return;
    try {
      await save.mutateAsync({
        gate,
        status: form.status,
        receivedOn: form.status === "Received" ? form.receivedOn || todayDubai() : null,
        reference: form.reference || null,
        note: doc?.note ?? null,
      });
      toast(`G${gate} documentation saved`);
      setForm(null);
    } catch (err) {
      toast(err instanceof ApiError ? err.message : "Could not save.", "error");
    }
  }

  if (form) {
    return (
      <div className="flex flex-wrap items-center gap-2">
        <SelectInput value={form.status} onChange={(e) => setForm({ ...form, status: e.target.value })} aria-label="Documentation status" className="mt-0 w-36">
          <option value="Pending">Pending</option>
          <option value="Received">Received</option>
          <option value="NotApplicable">Not Applicable</option>
        </SelectInput>
        {form.status === "Received" && (
          <TextInput type="date" value={form.receivedOn} onChange={(e) => setForm({ ...form, receivedOn: e.target.value })} aria-label="Received on" className="mt-0 w-40" />
        )}
        <TextInput placeholder="Document reference" value={form.reference} onChange={(e) => setForm({ ...form, reference: e.target.value })} aria-label="Document reference" className="mt-0 w-48" />
        <Button busy={save.isPending} onClick={submit}>Save</Button>
        <Button variant="secondary" onClick={() => setForm(null)}>Cancel</Button>
      </div>
    );
  }

  const status = doc?.status ?? "Pending";
  return (
    <button
      type="button"
      disabled={!canEdit}
      onClick={() => setForm({ status, receivedOn: doc?.receivedOn ?? todayDubai(), reference: doc?.reference ?? "" })}
      className={`rounded-md border px-2.5 py-1 text-left text-sm ${status === "Pending" ? "border-ongoing/40 text-ongoing" : "border-line text-muted"} enabled:hover:border-violet`}
    >
      Documentation: <span className="font-semibold">{docLabel[status]}</span>
      {status === "Received" && doc?.receivedOn && ` · ${formatDate(doc.receivedOn)}`}
      {doc?.reference && ` · ${doc.reference}`}
    </button>
  );
}
