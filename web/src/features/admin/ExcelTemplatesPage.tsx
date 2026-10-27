import { useEffect, useRef, useState } from "react";
import { Link } from "react-router";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api, download, upload } from "../../api/client";
import type { ExcelTemplateInfo } from "../../api/types";
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
