import { useEffect, useState, type FormEvent } from "react";
import { useNavigate, useParams, useSearchParams } from "react-router";
import { useQueryClient } from "@tanstack/react-query";
import { api, ApiError } from "../../api/client";
import type { AssetProfile, AssetType } from "../../api/types";
import { useDisciplines, useLineCodes, useOptions, usePipeSpecs, useUnit } from "../../app/useCatalog";
import { Button } from "../../components/Button";
import { Field, SelectInput, TextInput } from "../../components/Field";
import { PageHeader } from "../../components/PageHeader";
import { useToast } from "../../components/Toast";
import { formatSize, parseLineNumber } from "../lines/lineNumber";

/** New equipment: the Equipment ID is generated (prefix of the discipline, EMP, unit, next number). */
export function NewAssetPage() {
  const unitId = Number(useParams().unitId);
  const [params] = useSearchParams();
  const type = (params.get("type") as AssetType) || "Equipment";
  const { unit } = useUnit(unitId);
  const disciplines = useDisciplines();
  const options = useOptions();
  const codes = useLineCodes();
  const specs = usePipeSpecs();
  const navigate = useNavigate();
  const toast = useToast();
  const client = useQueryClient();
  const [form, setForm] = useState({ disciplineId: 0, tag: "", description: "", priority: "", length: "", pid: "" });
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();
  const [similar, setSimilar] = useState<string>();

  const available = (disciplines.data ?? []).filter((d) => d.assetType === type);
  const chosen = available.find((d) => d.id === form.disciplineId);
  const isLine = type === "Pipeline";
  const parts = isLine && form.tag.trim() ? parseLineNumber(form.tag) : null;
  const wrongUnit = parts && unit && parts.unit !== unit.number;

  // Lines have one discipline (Piping): choose it for the user.
  useEffect(() => {
    if (isLine && !form.disciplineId && available.length > 0) setForm((f) => ({ ...f, disciplineId: available[0].id }));
  }, [isLine, available, form.disciplineId]);

  async function submit(e: FormEvent, allowSimilar = false) {
    e.preventDefault();
    setSimilar(undefined);
    setBusy(true);
    setError(undefined);
    try {
      const created = await api<AssetProfile>("/api/assets", {
        method: "POST",
        body: {
          type, unitId, disciplineId: form.disciplineId, tag: form.tag, description: form.description || null, priority: form.priority || null,
          lengthM: isLine && form.length.trim() ? Number(form.length.replace(",", ".")) : null,
          pidNumber: isLine ? form.pid.trim() || null : null,
          allowSimilar,
        },
      });
      client.invalidateQueries({ queryKey: ["register"] });
      client.invalidateQueries({ queryKey: ["unit-summary"] });
      toast(`${created.code} created`);
      navigate(`/assets/${created.id}`);
    } catch (err) {
      if (err instanceof ApiError && err.status === 409 && isLine && !allowSimilar && err.message.includes("written differently")) setSimilar(err.message);
      else setError(err instanceof ApiError ? err.message : "Could not create it.");
      setBusy(false);
    }
  }

  return (
    <div className="max-w-xl">
      <PageHeader title={type === "ElectricalInstrument" ? "New E&I item" : isLine ? "New line" : "New equipment"} description={unit ? `${unit.name} — the Equipment ID is assigned automatically.` : undefined} />
      <form onSubmit={submit} className="mt-6 space-y-4">
        {!(isLine && available.length === 1) && <Field label="Discipline" hint={chosen ? (chosen.prefix ? `ID will look like ${chosen.prefix}-EMP-${String(unit?.number ?? 0).padStart(3, "0")}-###` : "This discipline has no ID prefix yet: ask an administrator.") : undefined}>
          <SelectInput required value={form.disciplineId || ""} onChange={(e) => setForm({ ...form, disciplineId: Number(e.target.value) })}>
            <option value="" disabled>Choose a discipline</option>
            {available.map((d) => <option key={d.id} value={d.id}>{d.name}</option>)}
          </SelectInput>
        </Field>}
        {isLine ? (
          <>
            <Field label="Line number" hint={`Unit - sequence - service - size" - spec, e.g. ${unit?.number ?? 39}-0218-LS-16"-61055-P.`}>
              <TextInput required maxLength={55} value={form.tag} onChange={(e) => setForm({ ...form, tag: e.target.value })} className="font-mono" />
            </Field>
            {form.tag.trim() && (
              <div className={`rounded-md border px-3 py-2.5 ${parts && !wrongUnit ? "border-line bg-surface" : "border-hold/40 bg-hold/5"}`} aria-live="polite">
                {!parts ? (
                  <p className="text-hold">Size and spec cannot be read yet. Check the format, including the inch mark (").</p>
                ) : wrongUnit ? (
                  <p className="text-hold">This line number starts with {parts.unit}, but you are adding it to {unit?.name}.</p>
                ) : (
                  <dl className="grid grid-cols-2 gap-x-4 gap-y-2 sm:grid-cols-3">
                    <Part label="Size" value={formatSize(parts.sizeIn)} />
                    <Part label="Pipe spec" value={parts.spec} meaning={specs.find(parts.spec) ? [specs.find(parts.spec)!.pressureClass, specs.find(parts.spec)!.flangeType, specs.find(parts.spec)!.gasket].filter(Boolean).join(" · ") || undefined : "Not in the pipe spec list"} />
                    <Part label="Service" value={parts.service} meaning={codes.describe("Service", parts.service) ?? "Not in the line codes"} />
                  </dl>
                )}
              </div>
            )}
            <div className="grid gap-4 sm:grid-cols-[10rem_1fr]">
              <Field label="Length (m)" hint="Optional.">
                <TextInput inputMode="decimal" value={form.length} onChange={(e) => setForm({ ...form, length: e.target.value })} />
              </Field>
              <Field label="P&ID number" hint="Optional; the drawing the line is on.">
                <TextInput maxLength={50} value={form.pid} onChange={(e) => setForm({ ...form, pid: e.target.value })} />
              </Field>
            </div>
          </>
        ) : (
          <Field label="Equipment tag" hint="The physical tag, e.g. 39-V-101.">
            <TextInput required maxLength={55} value={form.tag} onChange={(e) => setForm({ ...form, tag: e.target.value })} />
          </Field>
        )}
        <Field label="Description">
          <TextInput maxLength={255} value={form.description} onChange={(e) => setForm({ ...form, description: e.target.value })} />
        </Field>
        <Field label="Priority">
          <SelectInput value={form.priority} onChange={(e) => setForm({ ...form, priority: e.target.value })}>
            <option value="">—</option>
            {(options.data?.Priority ?? []).map((p) => <option key={p}>{p}</option>)}
          </SelectInput>
        </Field>
        {error && <p className="text-hold">{error}</p>}
        {similar && (
          <div className="rounded-md border border-ongoing/40 bg-ongoing/5 p-3">
            <p className="text-ink">{similar}</p>
            <Button type="button" variant="secondary" className="mt-2" busy={busy} onClick={(e) => submit(e as unknown as FormEvent, true)}>It is a different line: create it</Button>
          </div>
        )}
        <div className="flex justify-end gap-2">
          <Button type="button" variant="secondary" onClick={() => navigate(-1)}>Cancel</Button>
          <Button type="submit" busy={busy} disabled={!chosen?.prefix || (isLine && (!parts || !!wrongUnit))}>Create</Button>
        </div>
      </form>
    </div>
  );
}

function Part({ label, value, meaning }: { label: string; value: string; meaning?: string }) {
  return (
    <div className="min-w-0">
      <dt className="text-sm text-muted">{label}</dt>
      <dd className="font-semibold text-ink">{value}</dd>
      {meaning && <dd className={`text-sm leading-snug ${meaning.startsWith("Not in the") ? "text-ongoing" : "text-muted"}`}>{meaning}</dd>}
    </div>
  );
}
