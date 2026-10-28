import { useEffect, useRef, useState } from "react";
import { Link } from "react-router";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api, download, upload } from "../../api/client";
import type { ExcelTemplateInfo, PipingTemplateInfo } from "../../api/types";
import { Button } from "../../components/Button";
import { Field, TextInput } from "../../components/Field";
import { PageHeader } from "../../components/PageHeader";
import { formatDateTime } from "../../components/dates";
import { useToast } from "../../components/Toast";
import { AdminTabs } from "./AdminTabs";

/**
 * The clean client template of each unit (MasterList table with one empty row, Dashboard, Categories).
 * The first template of a unit also brings its dashboard categories into the app.
 */
export function ExcelTemplatesPage() {
  const toast = useToast();
  const queryClient = useQueryClient();
  const templates = useQuery({ queryKey: ["excel-templates"], queryFn: ({ signal }) => api<ExcelTemplateInfo[]>("/api/excel-templates", { signal }) });
  const settings = useQuery({ queryKey: ["excel-settings"], queryFn: ({ signal }) => api<{ maxAgeHours: number }>("/api/settings/excel", { signal }) });
  const [hours, setHours] = useState("");
  useEffect(() => {
    if (settings.data) setHours(String(settings.data.maxAgeHours));
  }, [settings.data]);

  const saveHours = useMutation({
    mutationFn: () => api<{ maxAgeHours: number }>("/api/settings/excel", { method: "PUT", body: { maxAgeHours: Number(hours) } }),
    onSuccess: (d) => { queryClient.setQueryData(["excel-settings"], d); toast("Age limit saved"); },
    onError: (e) => toast(e.message, "error"),
  });

  return (
    <div>
      <AdminTabs />
      <PageHeader title="Excel templates" description="Upload each unit's clean client template. Exports always start from it; rows, Key column, dropdowns and categories are added by the app." />

      {templates.data && (
        <div className="mt-6 overflow-x-auto rounded-lg border border-line bg-panel">
          <table className="w-full min-w-[44rem] text-left">
            <thead className="text-sm text-muted">
              <tr>{["Unit", "Template", "Categories", ""].map((h) => <th key={h} className="border-b border-line px-4 py-2.5 font-semibold">{h}</th>)}</tr>
            </thead>
            <tbody>
              {templates.data.map((t) => <TemplateRow key={t.unitId} t={t} />)}
            </tbody>
          </table>
        </div>
      )}

      <PipingTemplateSection />

      <section className="mt-6 max-w-xl rounded-lg border border-line bg-panel p-5">
        <h2 className="text-xl font-semibold">Age limit of uploaded files</h2>
        <p className="mt-1 text-muted">A downloaded file older than this is refused, because the data may have moved on meanwhile.</p>
        <form className="mt-4 flex items-end gap-3" onSubmit={(e) => { e.preventDefault(); saveHours.mutate(); }}>
          <Field label="Hours">
            <TextInput type="number" min={1} max={720} value={hours} onChange={(e) => setHours(e.target.value)} className="w-28" />
          </Field>
          <Button type="submit" busy={saveHours.isPending} disabled={!hours || Number(hours) === settings.data?.maxAgeHours}>Save</Button>
        </form>
      </section>
    </div>
  );
}

function TemplateRow({ t }: { t: ExcelTemplateInfo }) {
  const input = useRef<HTMLInputElement>(null);
  const toast = useToast();
  const queryClient = useQueryClient();
  const send = useMutation({
    mutationFn: (file: File) => upload<ExcelTemplateInfo>(`/api/excel-templates/${t.unitId}`, file, "PUT"),
    onSuccess: (d) => {
      queryClient.invalidateQueries({ queryKey: ["excel-templates"] });
      queryClient.invalidateQueries({ queryKey: ["excel", t.unitId] });
      toast(`Template of ${d.unitName} saved${d.categories ? ` · ${d.categories} categories` : ""}`);
    },
    onError: (e) => toast(e.message, "error"),
  });

  return (
    <tr className="border-b border-line last:border-0">
      <td className="px-4 py-3 font-semibold text-ink">{t.unitName}</td>
      <td className="px-4 py-3">
        {t.fileName ? (
          <>
            <button type="button" className="font-semibold text-violet hover:underline" onClick={() => download(`/api/excel-templates/${t.unitId}/file`).catch((e) => toast(e.message, "error"))}>{t.fileName}</button>
            <span className="block text-sm text-muted">{formatDateTime(t.uploadedAtUtc)} by {t.uploadedBy}</span>
          </>
        ) : (
          <span className="text-muted">No template — this unit cannot be exported yet</span>
        )}
      </td>
      <td className="px-4 py-3">
        <Link to={`/u/${t.unitId}/categories`} className="font-semibold text-violet">{t.categories} {t.categories === 1 ? "category" : "categories"}</Link>
      </td>
      <td className="px-4 py-3 text-right">
        <Button variant="secondary" busy={send.isPending} onClick={() => input.current?.click()}>{t.fileName ? "Replace" : "Upload"}</Button>
        <input ref={input} type="file" accept=".xlsx" className="hidden" onChange={(e) => { const f = e.target.files?.[0]; if (f) send.mutate(f); e.target.value = ""; }} />
      </td>
    </tr>
  );
}

/**
 * The piping line list template, one for the whole project. Without an upload the built-in default is used;
 * download it, change formats or Summary formulas, and upload it back.
 */
function PipingTemplateSection() {
  const input = useRef<HTMLInputElement>(null);
  const toast = useToast();
  const queryClient = useQueryClient();
  const info = useQuery({ queryKey: ["piping-template"], queryFn: ({ signal }) => api<PipingTemplateInfo>("/api/excel-templates/piping", { signal }) });
  const [notes, setNotes] = useState<string[]>([]);
  const send = useMutation({
    mutationFn: (file: File) => upload<PipingTemplateInfo>("/api/excel-templates/piping", file, "PUT"),
    onSuccess: (d) => { queryClient.setQueryData(["piping-template"], d); setNotes(d.notes); toast("Piping template saved"); },
    onError: (e) => toast(e.message, "error"),
  });
  const reset = useMutation({
    mutationFn: () => api<PipingTemplateInfo>("/api/excel-templates/piping", { method: "DELETE" }),
    onSuccess: (d) => { queryClient.setQueryData(["piping-template"], d); setNotes([]); toast("Back to the default template"); },
    onError: (e) => toast(e.message, "error"),
  });
  const t = info.data;

  return (
    <section className="mt-6 rounded-lg border border-line bg-panel p-5">
      <h2 className="text-xl font-semibold">Piping line list (all units)</h2>
      <p className="mt-1 max-w-3xl text-muted">
        One template for every unit. Columns are recognised by their header (Tag No, P&ID Number, Current Activity, EM Walkdown Status, Bulk liquid drained,
        Inspection Done ?, Cleaning Required ?, Cleaned?…) and the table keeps one empty row. In the Summary, the row with <code>{"{CODE}"}</code> in column B is
        copied for each service code with its formulas; <code>{"{NOTES}"}</code> is where the notes go; <code>{"{UNIT}"}</code> and <code>{"{DATE}"}</code> are
        replaced. Files already downloaded keep importing with their own layout.
      </p>
      {t && (
        <div className="mt-4 flex flex-wrap items-center gap-3">
          <div className="min-w-0 flex-1">
            <button type="button" className="font-semibold text-violet hover:underline" onClick={() => download("/api/excel-templates/piping/file").catch((e) => toast(e.message, "error"))}>
              {t.fileName}
            </button>
            <span className="block text-sm text-muted">{t.isDefault ? "Built-in default (the engineers' U39 layout)" : `${formatDateTime(t.uploadedAtUtc)} by ${t.uploadedBy}`}</span>
          </div>
          <Button variant="secondary" busy={send.isPending} onClick={() => input.current?.click()}>{t.isDefault ? "Upload" : "Replace"}</Button>
          {!t.isDefault && (
            <Button variant="secondary" busy={reset.isPending} onClick={() => window.confirm("Go back to the built-in default template?") && reset.mutate()}>Use default</Button>
          )}
          <input ref={input} type="file" accept=".xlsx" className="hidden" onChange={(e) => { const f = e.target.files?.[0]; if (f) send.mutate(f); e.target.value = ""; }} />
        </div>
      )}
      {notes.length > 0 && (
        <ul className="mt-3 list-disc space-y-1 pl-5 text-sm text-ongoing">
          {notes.map((n) => <li key={n}>{n}</li>)}
        </ul>
      )}
    </section>
  );
}
