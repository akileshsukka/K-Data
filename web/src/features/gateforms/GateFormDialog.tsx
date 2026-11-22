import { useEffect, useState } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { api, download } from "../../api/client";
import type { GateFormKind, GateFormPrepared, GateFormRow } from "../../api/types";
import { Button } from "../../components/Button";
import { Dialog } from "../../components/Dialog";
import { TextInput } from "../../components/Field";
import { useToast } from "../../components/Toast";

type Editable = Pick<GateFormRow, "assetId" | "recordNo" | "date" | "section" | "workPack" | "unitId">;

const titles: Record<GateFormKind, string> = {
  Mechanical: "Gate verification forms",
  Electrical: "Gate electrical verification forms",
};

/**
 * Word file of the printable section acceptance gate, one page per selected asset. Each row starts from what was saved
 * for the asset (or the tag / P&ID number / unit Unit ID); date and the other fields can be set for all at once or row by
 * row. Generating saves the values with the assets (roles that may edit), and an empty Record No. takes the next one of
 * its discipline and unit (ITR-STAT-541-001). "Save without printing" keeps Record Nos. typed for forms made outside the app.
 */
export function GateFormDialog({ open, onClose, unitId, kind, assetIds }: { open: boolean; onClose: () => void; unitId: number; kind: GateFormKind; assetIds: string[] }) {
  const toast = useToast();
  const queryClient = useQueryClient();
  const prepared = useQuery({
    queryKey: ["gate-form-prepare", unitId, kind, assetIds],
    queryFn: ({ signal }) => api<GateFormPrepared>(`/api/units/${unitId}/gate-forms/prepare`, { method: "POST", body: { kind, assetIds }, signal }),
    enabled: open && assetIds.length > 0,
    // Loaded once per opening: coming back from Excel after copying must not reload the rows over what was typed.
    staleTime: Infinity,
    gcTime: 0,
    refetchOnWindowFocus: false,
    refetchOnReconnect: false,
  });
  const [rows, setRows] = useState<Editable[]>([]);
  const [all, setAll] = useState({ date: "", workPack: "", unitId: "" });
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    if (prepared.data) setRows(prepared.data.rows.map(({ assetId, recordNo, date, section, workPack, unitId }) => ({ assetId, recordNo, date, section, workPack, unitId })));
  }, [prepared.data]);
  useEffect(() => {
    if (!open) setAll({ date: "", workPack: "", unitId: "" });
  }, [open]);

  const data = prepared.data;
  const has = (f: string) => data?.fields.includes(f as never) ?? false;
  const info = new Map(data?.rows.map((r) => [r.assetId, r]));

  function update(i: number, patch: Partial<Editable>) {
    setRows((prev) => prev.map((r, k) => (k === i ? { ...r, ...patch } : r)));
  }
  function applyToAll(key: "date" | "workPack" | "unitId") {
    const value = all[key].trim() || null;
    setRows((prev) => prev.map((r) => ({ ...r, [key]: value })));
  }

  const body = () => ({
    kind,
    rows: rows.map((r) => ({ ...r, recordNo: r.recordNo?.trim() || null, date: r.date || null, workPack: r.workPack || null, unitId: r.unitId || null })),
  });
  const done = () => {
    queryClient.invalidateQueries({ queryKey: ["gate-form-prepare"] });
    queryClient.invalidateQueries({ queryKey: ["asset"] });
    onClose();
  };

  async function generate() {
    if (!data) return;
    if (has("SOURCE") && !data.source
      && !window.confirm(`The source document of this unit is not set (Admin → Gate forms). The forms will be printed with the source left empty.\n\nGenerate anyway?`)) return;
    setBusy(true);
    try {
      const name = await download(`/api/units/${unitId}/gate-forms/generate`, body());
      toast(`${name} downloaded${data.canSave ? "; the values were saved with the assets" : ""}`);
      done();
    } catch (e) {
      toast((e as Error).message, "error");
    } finally {
      setBusy(false);
    }
  }

  async function saveOnly() {
    setBusy(true);
    try {
      await api<void>(`/api/units/${unitId}/gate-forms/save`, { method: "POST", body: body() });
      toast("Values saved with the assets (no file printed)");
      done();
    } catch (e) {
      toast((e as Error).message, "error");
    } finally {
      setBusy(false);
    }
  }

  return (
    <Dialog open={open} onClose={onClose} title={titles[kind]} wide>
      {prepared.isLoading && <p className="text-muted">Loading…</p>}
      {prepared.error && <p className="text-hold">{prepared.error.message}</p>}
      {data && (
        <div className="space-y-4">
          <p className="text-muted">
            One page per asset, in the template <span className="font-semibold text-ink">{data.templateName}</span>
            {data.isBuiltIn ? " (built-in)" : ""}. Empty fields print the blank line of the form.
            {!data.canSave && " Your role cannot save these values: they are used for this file only."}
          </p>

          {has("SOURCE") && !data.source && (
            <p className="rounded-md border border-ongoing/40 bg-ongoing/10 px-3 py-2 text-ongoing">
              The source document of this unit is not set yet (Admin → Gate forms): it will be printed empty.
            </p>
          )}
          {data.skipped.length > 0 && (
            <details className="rounded-md border border-line px-3 py-2">
              <summary className="cursor-pointer font-semibold text-ink">{data.skipped.length} selected asset{data.skipped.length === 1 ? "" : "s"} left out</summary>
              <ul className="mt-2 space-y-1 text-sm text-muted">
                {data.skipped.map((s) => <li key={s.code}><span className="font-semibold text-ink">{s.tag ?? s.code}</span> — {s.reason}</li>)}
              </ul>
            </details>
          )}

          {rows.length === 0 ? (
            <p className="text-hold">None of the selected assets has this form.</p>
          ) : (
            <>
              <div className="flex flex-wrap items-end gap-3 rounded-md bg-violet-soft/40 p-3">
                <AllField label="Date for all" type="date" value={all.date} onChange={(v) => setAll({ ...all, date: v })} onApply={() => applyToAll("date")} />
                {has("WORKPACK") && <AllField label="Work pack / P&ID rev. for all" value={all.workPack} onChange={(v) => setAll({ ...all, workPack: v })} onApply={() => applyToAll("workPack")} />}
                {has("UNIT_ID") && <AllField label="Unit ID for all" value={all.unitId} onChange={(v) => setAll({ ...all, unitId: v })} onApply={() => applyToAll("unitId")} />}
              </div>

              <div className="max-h-[55vh] overflow-auto rounded-md border border-line">
                <table className="w-full min-w-[56rem] text-left text-sm">
                  <thead className="sticky top-0 bg-panel text-muted">
                    <tr>
                      {["Asset", "Record No.", "Date", "System / circuit / section", ...(has("WORKPACK") ? ["Work pack / P&ID rev."] : []), ...(has("UNIT_ID") ? ["Unit ID"] : [])]
                        .map((h) => <th key={h} className="border-b border-line px-2 py-2 font-semibold">{h}</th>)}
                    </tr>
                  </thead>
                  <tbody>
                    {rows.map((r, i) => {
                      const a = info.get(r.assetId)!;
                      return (
                        <tr key={r.assetId} className="border-b border-line last:border-0 align-top">
                          <td className="px-2 py-1.5">
                            <span className="block font-semibold text-ink">{a.tag ?? a.code}</span>
                            <span className="block max-w-56 truncate text-muted" title={a.description ?? undefined}>{a.description}</span>
                          </td>
                          <td className="px-2 py-1.5">
                            <Cell value={r.recordNo} onChange={(v) => update(i, { recordNo: v })}
                              placeholder={a.recordNoNext ? `Next: ${a.recordNoNext}` : data.canSave ? "Type it" : ""}
                              title={a.recordNoNext ? "Left empty, the asset gets this number when printed. Type another one to use it instead." : "This discipline has no Record No. code (Admin → Gate forms): type it."} />
                          </td>
                          <td className="px-2 py-1.5"><Cell type="date" value={r.date} onChange={(v) => update(i, { date: v })} /></td>
                          <td className="px-2 py-1.5">
                            <Cell value={r.section} onChange={(v) => update(i, { section: v ?? "" })} placeholder={a.sectionDefault} />
                          </td>
                          {has("WORKPACK") && <td className="px-2 py-1.5"><Cell value={r.workPack} onChange={(v) => update(i, { workPack: v })} placeholder={a.workPackDefault ?? "Paste here"} /></td>}
                          {has("UNIT_ID") && <td className="px-2 py-1.5"><Cell value={r.unitId} onChange={(v) => update(i, { unitId: v })} placeholder={a.unitIdDefault ?? ""} /></td>}
                        </tr>
                      );
                    })}
                  </tbody>
                </table>
              </div>
            </>
          )}

          <div className="flex justify-end gap-3">
            <Button variant="secondary" onClick={onClose}>Cancel</Button>
            {data.canSave && (
              <Button variant="secondary" disabled={busy || rows.length === 0} onClick={saveOnly}
                title="Keep the values typed (e.g. the Record No. of a form made outside the app) without printing. Empty Record Nos. stay empty.">
                Save without printing
              </Button>
            )}
            <Button busy={busy} disabled={rows.length === 0} onClick={generate}>Generate Word ({rows.length} page{rows.length === 1 ? "" : "s"})</Button>
          </div>
        </div>
      )}
    </Dialog>
  );
}

function AllField({ label, value, onChange, onApply, type = "text" }: { label: string; value: string; onChange: (v: string) => void; onApply: () => void; type?: string }) {
  return (
    <div className="flex items-end gap-1.5">
      <label className="block">
        <span className="text-sm font-semibold text-ink">{label}</span>
        <TextInput type={type} value={value} onChange={(e) => onChange(e.target.value)} className="w-48" />
      </label>
      <Button variant="secondary" onClick={onApply} title="Write this value in every row (empty clears them)">Apply</Button>
    </div>
  );
}

function Cell({ value, onChange, type = "text", placeholder, title }: { value: string | null; onChange: (v: string | null) => void; type?: string; placeholder?: string; title?: string }) {
  return (
    <input
      type={type}
      value={value ?? ""}
      placeholder={placeholder}
      title={title}
      maxLength={200}
      onChange={(e) => onChange(e.target.value === "" ? null : e.target.value)}
      className="block min-h-9 w-full min-w-32 rounded-md border border-line bg-panel px-2 text-[14px] text-text placeholder:text-muted/70 focus:border-violet focus:outline-none"
    />
  );
}
