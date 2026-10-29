import { useMemo, useState } from "react";
import { Link, useLocation, useParams } from "react-router";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { api, ApiError } from "../../api/client";
import { Perm, type BulkResult, type CategoryOption, type RegisterRow, type SearchHit } from "../../api/types";
import { useFields, useOptions, useUnit } from "../../app/useCatalog";
import { useSession } from "../../app/session";
import { AssetSearch } from "../../components/AssetSearch";
import { Button } from "../../components/Button";
import { Field, SelectInput, TextInput } from "../../components/Field";
import { PageHeader } from "../../components/PageHeader";
import { useToast } from "../../components/Toast";
import { FieldInput } from "../asset/FieldInput";

interface Picked {
  id: string;
  label: string;
  detail: string;
}

const PRESERVED = "PreservedAt";
const CATEGORY = "DashboardCategory";

/** Change one field on many assets: pick them, choose field and value, preview, confirm. Every change is audited. */
export function BulkEditPage() {
  const unitId = Number(useParams().unitId);
  const { unit } = useUnit(unitId);
  const location = useLocation() as { state?: { ids?: string[] } };
  const session = useSession();
  const fields = useFields();
  const options = useOptions();
  const toast = useToast();
  const client = useQueryClient();

  const register = useQuery({
    queryKey: ["register", unitId, "bulk-all"],
    queryFn: async ({ signal }) => {
      const [eq, ei] = await Promise.all([
        api<RegisterRow[]>(`/api/units/${unitId}/assets?type=Equipment`, { signal }),
        api<RegisterRow[]>(`/api/units/${unitId}/assets?type=ElectricalInstrument`, { signal }),
      ]);
      return [...eq, ...ei];
    },
  });

  const [picked, setPicked] = useState<Picked[] | null>(null);
  const initial = useMemo<Picked[]>(() => {
    const ids = new Set(location.state?.ids ?? []);
    return (register.data ?? []).filter((r) => ids.has(r.id)).map((r) => ({ id: r.id, label: r.tag ?? r.code, detail: r.code }));
  }, [register.data, location.state]);
  const list = picked ?? initial;

  const [fieldKey, setFieldKey] = useState("");
  const [value, setValue] = useState("");
  const [paste, setPaste] = useState("");
  const [preview, setPreview] = useState<BulkResult | null>(null);
  const [busy, setBusy] = useState(false);

  const bulkFields = (fields.data ?? []).filter((f) => f.bulkEditable && session.can(f.permission));
  const field = bulkFields.find((f) => f.key === fieldKey);
  const canPreserve = session.can("assets.markPreserved");
  const canCategory = session.can(Perm.assetsEdit);
  const categories = useQuery({
    queryKey: ["category-options", unitId],
    queryFn: ({ signal }) => api<CategoryOption[]>(`/api/units/${unitId}/category-options`, { signal }),
    enabled: canCategory,
  });
  const holdOptions = (categories.data ?? []).filter((o) => o.section === "Hold");
  const naOptions = (categories.data ?? []).filter((o) => o.section === "NotApplicable");
  const ids = new Set(list.map((p) => p.id));

  function add(items: Picked[]) {
    const next = [...list];
    for (const i of items) if (!next.some((p) => p.id === i.id)) next.push(i);
    setPicked(next);
    setPreview(null);
  }

  function addFromPaste() {
    const tokens = paste.split(/[\n,;\t]+/).map((t) => t.trim().toLowerCase()).filter(Boolean);
    const rows = register.data ?? [];
    const found = rows.filter((r) => tokens.includes((r.tag ?? "").toLowerCase()) || tokens.includes(r.code.toLowerCase()));
    const missing = tokens.filter((t) => !rows.some((r) => (r.tag ?? "").toLowerCase() === t || r.code.toLowerCase() === t));
    add(found.map((r) => ({ id: r.id, label: r.tag ?? r.code, detail: r.code })));
    setPaste("");
    toast(missing.length ? `${found.length} added. Not found in ${unit?.name}: ${missing.slice(0, 5).join(", ")}${missing.length > 5 ? "…" : ""}` : `${found.length} added`, missing.length ? "info" : "success");
  }

  async function run(apply: boolean) {
    setBusy(true);
    try {
      const result = await api<BulkResult>(`/api/bulk/${apply ? "apply" : "preview"}`, {
        method: "POST",
        body: { assetIds: list.map((p) => p.id), field: fieldKey, value: value || null },
      });
      if (apply) {
        toast(`${result.changed} assets changed`);
        client.invalidateQueries({ queryKey: ["register"] });
        client.invalidateQueries({ queryKey: ["asset"] });
        client.invalidateQueries({ queryKey: ["unit-summary"] });
        client.invalidateQueries({ queryKey: ["dashboard"] });
        client.invalidateQueries({ queryKey: ["categories"] });
        setPreview(null);
        setPicked([]);
        setValue("");
      } else setPreview(result);
    } catch (err) {
      toast(err instanceof ApiError ? err.message : "Could not run the bulk edit.", "error");
    } finally {
      setBusy(false);
    }
  }

  return (
    <div>
      <PageHeader title="Bulk edit" description={`${unit?.name ?? ""} — change one field on many assets at once.`} />

      <div className="mt-6 grid gap-6 lg:grid-cols-[1fr_24rem]">
        <section className="min-w-0 space-y-4">
          <div className="rounded-lg border border-line bg-panel p-4">
            <h2 className="text-lg font-semibold">1. Choose the assets</h2>
            <div className="mt-3 grid gap-3 md:grid-cols-2">
              <div>
                <span className="text-sm font-semibold text-ink">Search and add</span>
                <AssetSearch unitId={unitId} excludeIds={ids} onPick={(h: SearchHit) => add([{ id: h.id, label: h.tag ?? h.code, detail: h.code }])} />
              </div>
              <div>
                <span className="text-sm font-semibold text-ink">Or paste tags from Excel</span>
                <div className="flex gap-2">
                  <TextInput value={paste} onChange={(e) => setPaste(e.target.value)} placeholder="39-E-101, 39-E-102…" className="mt-0" aria-label="Paste tags" />
                  <Button variant="secondary" disabled={!paste.trim() || !register.data} onClick={addFromPaste}>Add</Button>
                </div>
              </div>
            </div>
            <p className="mt-3 text-sm text-muted">
              You can also tick rows in a register and choose “Bulk edit selected”. <Link to={`/u/${unitId}/equipment`} className="font-semibold text-violet">Open the register</Link>
            </p>
          </div>

          <div className="rounded-lg border border-line bg-panel">
            <header className="flex items-center justify-between border-b border-line px-4 py-2.5">
              <h2 className="font-semibold">{list.length} selected</h2>
              {list.length > 0 && <Button variant="quiet" onClick={() => { setPicked([]); setPreview(null); }}>Clear</Button>}
            </header>
            {list.length === 0 ? (
              <p className="px-4 py-6 text-muted">No assets selected yet.</p>
            ) : preview ? (
              <table className="w-full border-separate border-spacing-0 text-left text-[15px]">
                <thead className="font-display text-muted">
                  <tr>
                    <th className="border-b border-line px-4 py-2 font-semibold">Asset</th>
                    <th className="border-b border-line px-4 py-2 font-semibold">Now</th>
                    <th className="border-b border-line px-4 py-2 font-semibold">After</th>
                  </tr>
                </thead>
                <tbody>
                  {preview.rows.map((r) => (
                    <tr key={r.assetId} className={r.outcome === "Change" ? "" : "text-muted"}>
                      <td className="border-b border-line px-4 py-2">
                        <span className="font-semibold">{r.tag ?? r.code}</span>
                        {r.outcome !== "Change" && <span className="block text-sm">{r.reason ?? r.outcome}</span>}
                      </td>
                      <td className="border-b border-line px-4 py-2 whitespace-pre-line">{r.current ?? "—"}</td>
                      <td className="border-b border-line px-4 py-2 whitespace-pre-line">
                        {r.outcome === "Change" ? (r.new ?? "empty") : "no change"}
                        {r.outcome === "Change" && r.reason && <span className="block text-sm text-muted">{r.reason}</span>}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            ) : (
              <ul className="flex flex-wrap gap-2 p-4">
                {list.map((p) => (
                  <li key={p.id} className="inline-flex items-center gap-1 rounded-md border border-line bg-surface py-1 pr-1 pl-2.5 text-[15px]">
                    <span className="font-semibold">{p.label}</span>
                    <button className="rounded px-1.5 text-muted hover:bg-hold/10 hover:text-hold" aria-label={`Remove ${p.label}`} onClick={() => setPicked(list.filter((x) => x.id !== p.id))}>×</button>
                  </li>
                ))}
              </ul>
            )}
          </div>
        </section>

        <aside className="h-fit rounded-lg border border-line bg-panel p-4">
          <h2 className="text-lg font-semibold">2. Field and value</h2>
          <div className="mt-3 space-y-3">
            <Field label="Field">
              <SelectInput value={fieldKey} onChange={(e) => { setFieldKey(e.target.value); setValue(""); setPreview(null); }}>
                <option value="" disabled>Choose a field</option>
                {canPreserve && <option value={PRESERVED}>Mark as preserved (date)</option>}
                {canCategory && <option value={CATEGORY}>Dashboard category (Hold / N/A)</option>}
                {["Gates", "Status", "Client tracking", "Identification"].map((sec) => (
                  <optgroup key={sec} label={sec}>
                    {bulkFields.filter((f) => f.section === sec).map((f) => <option key={f.key} value={f.key}>{f.label}</option>)}
                  </optgroup>
                ))}
              </SelectInput>
            </Field>
            {fieldKey === PRESERVED && (
              <Field label="Preserved from" hint="Empty removes the mark.">
                <TextInput type="date" value={value} onChange={(e) => { setValue(e.target.value); setPreview(null); }} />
              </Field>
            )}
            {fieldKey === CATEGORY && (
              <Field label="Category" hint="None removes the category. Lines are skipped.">
                <SelectInput value={value} disabled={categories.isPending} onChange={(e) => { setValue(e.target.value); setPreview(null); }}>
                  <option value="">None</option>
                  {holdOptions.length > 0 && (
                    <optgroup label="Hold">
                      {holdOptions.map((o) => <option key={o.id} value={o.id}>{o.code} – {o.label}</option>)}
                    </optgroup>
                  )}
                  {naOptions.length > 0 && (
                    <optgroup label="Not Applicable">
                      {naOptions.map((o) => <option key={o.id} value={o.id}>{o.code} – {o.label}</option>)}
                    </optgroup>
                  )}
                </SelectInput>
                {categories.data?.length === 0 && <span className="mt-1 block text-sm text-muted">This unit has no categories yet.</span>}
              </Field>
            )}
            {field && (
              <label className="block">
                <span className="text-sm font-semibold text-ink">New value</span>
                <FieldInput field={field} value={value} onChange={(v) => { setValue(v); setPreview(null); }} options={options.data ?? {}} />
                <span className="mt-1 block text-sm text-muted">Empty clears the field.</span>
              </label>
            )}
          </div>
          {preview && (
            <p className="mt-4 rounded-md bg-surface px-3 py-2 text-[15px]">
              <strong>{preview.changed}</strong> will change, {preview.unchanged} already have this value, {preview.skipped} skipped.
            </p>
          )}
          <div className="mt-4 flex flex-col gap-2">
            <Button variant="secondary" disabled={!fieldKey || list.length === 0} busy={busy && !preview} onClick={() => run(false)}>Preview</Button>
            <Button disabled={!preview || preview.changed === 0} busy={busy && !!preview} onClick={() => run(true)}>
              {preview ? `Change ${preview.changed} assets` : "Apply"}
            </Button>
          </div>
        </aside>
      </div>
    </div>
  );
}
