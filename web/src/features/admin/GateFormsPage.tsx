import { useEffect, useRef, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api, download, upload } from "../../api/client";
import type { GateFormTemplateSlot, GateFormTemplateVersion, GateFormUnitSetting, RecordNoCode, RecordNoSettings } from "../../api/types";
import { Button } from "../../components/Button";
import { TextInput } from "../../components/Field";
import { PageHeader } from "../../components/PageHeader";
import { formatDateTime } from "../../components/dates";
import { useToast } from "../../components/Toast";
import { AdminTabs } from "./AdminTabs";

const kindLabel = { Mechanical: "Mechanical (G1–G8) · Equipment and Pipeline registers", Electrical: "Electrical motors (E1–E8) · E&I register" } as const;

const markers: [string, string][] = [
  ["{{UNIT}}", "Unit number (header)"],
  ["{{SOURCE}}", "Source document of the unit (below)"],
  ["{{RECORD_NO}}", "Record No."],
  ["{{DATE}}", "Date, 25/11/2026"],
  ["{{SECTION}}", "Tag or line number (required)"],
  ["{{WORKPACK}}", "Work pack / P&ID rev."],
  ["{{UNIT_ID}}", "Unit ID"],
];

/** Printable gate forms: values of each unit and the Word templates of each plant (versions, sample, built-in). */
export function GateFormsPage() {
  const settings = useQuery({ queryKey: ["gate-form-settings"], queryFn: ({ signal }) => api<GateFormUnitSetting[]>("/api/gate-forms/settings", { signal }) });
  const templates = useQuery({ queryKey: ["gate-form-templates"], queryFn: ({ signal }) => api<GateFormTemplateSlot[]>("/api/gate-forms/templates", { signal }) });

  return (
    <div>
      <AdminTabs />
      <PageHeader title="Gate forms" description="Printable section acceptance gate forms (Word), generated from the registers with one page per selected asset." />

      <section className="mt-6 rounded-lg border border-line bg-panel">
        <h2 className="px-5 pt-5 text-xl font-semibold">Values of each unit</h2>
        <p className="px-5 pt-1 text-muted">Printed on every form of the unit. A unit without a source can still print its forms, with the source left empty, after a warning.</p>
        <div className="mt-3 overflow-x-auto">
          <table className="w-full min-w-[52rem] text-left">
            <thead className="text-sm text-muted">
              <tr>{["Unit", "Source – mechanical form", "Source – electrical form", "Unit ID", ""].map((h) => <th key={h} className="border-y border-line px-4 py-2.5 font-semibold">{h}</th>)}</tr>
            </thead>
            <tbody>{settings.data?.map((s) => <SettingRow key={s.unitId} s={s} />)}</tbody>
          </table>
        </div>
      </section>

      <RecordNumbersSection />

      <section className="mt-6 rounded-lg border border-line bg-panel p-5">
        <h2 className="text-xl font-semibold">Templates</h2>
        <p className="mt-1 max-w-4xl text-muted">
          A template is the client's Word form with markers where the app writes the values; everything else (fonts, colours, tables, logo) is printed as it is.
          To change one, download the template in use, edit it in Word, keep the markers and upload it. It is checked and kept as a new version:
          download its sample, then activate it. Headers and footers can only hold <code>{"{{UNIT}}"}</code> and <code>{"{{SOURCE}}"}</code>.
        </p>
        <dl className="mt-3 grid max-w-3xl grid-cols-[auto_1fr] gap-x-4 gap-y-0.5 text-sm">
          {markers.map(([m, d]) => <div key={m} className="contents"><dt><code>{m}</code></dt><dd className="text-muted">{d}</dd></div>)}
        </dl>
        <div className="mt-5 space-y-4">{templates.data?.map((t) => <TemplateSlot key={`${t.plantId}-${t.kind}`} t={t} />)}</div>
      </section>
    </div>
  );
}

const registerLabel: Record<string, string> = { Equipment: "Equipment", Pipeline: "Pipeline", ElectricalInstrument: "E&I" };

/** Record No. (ITR) of the forms: code of each discipline and where each counter is. */
function RecordNumbersSection() {
  const data = useQuery({ queryKey: ["record-numbers"], queryFn: ({ signal }) => api<RecordNoSettings>("/api/gate-forms/record-numbers", { signal }) });
  return (
    <section className="mt-6 rounded-lg border border-line bg-panel p-5">
      <h2 className="text-xl font-semibold">Record No. (ITR)</h2>
      <p className="mt-1 max-w-4xl text-muted">
        An asset printed with the Record No. empty gets the next one of its discipline and unit: <code>ITR-STAT-541-001</code>, <code>ITR-STAT-541-002</code>…
        Reprints keep it. It can always be typed by hand in the form dialog (with “Save without printing” for forms made outside the app);
        a typed number above the counter moves the counter up. A discipline without a code has its Record No. typed by hand.
      </p>
      <div className="mt-4 grid gap-6 lg:grid-cols-2">
        <table className="w-full text-left">
          <thead className="text-sm text-muted">
            <tr>{["Discipline", "Register", "Code", ""].map((h) => <th key={h} className="border-b border-line py-2 pr-3 font-semibold">{h}</th>)}</tr>
          </thead>
          <tbody>{data.data?.codes.map((c) => <CodeRow key={c.disciplineId} c={c} />)}</tbody>
        </table>
        <div>
          <table className="w-full text-left">
            <thead className="text-sm text-muted">
              <tr>{["Unit", "Code", "Last used", "Next"].map((h) => <th key={h} className="border-b border-line py-2 pr-3 font-semibold">{h}</th>)}</tr>
            </thead>
            <tbody>
              {data.data?.counters.map((c) => (
                <tr key={`${c.code}-${c.unitNumber}`} className="border-b border-line last:border-0">
                  <td className="py-2 pr-3">{c.unitNumber}</td>
                  <td className="py-2 pr-3">{c.code}</td>
                  <td className="py-2 pr-3">{String(c.lastNumber).padStart(3, "0")}</td>
                  <td className="py-2 pr-3 font-semibold text-ink">{c.next}</td>
                </tr>
              ))}
            </tbody>
          </table>
          {data.data?.counters.length === 0 && <p className="mt-2 text-muted">No Record No. given yet: each code and unit starts at 001.</p>}
        </div>
      </div>
    </section>
  );
}

function CodeRow({ c }: { c: RecordNoCode }) {
  const toast = useToast();
  const queryClient = useQueryClient();
  const [code, setCode] = useState(c.code ?? "");
  useEffect(() => setCode(c.code ?? ""), [c]);
  const save = useMutation({
    mutationFn: () => api<RecordNoCode>(`/api/gate-forms/record-numbers/disciplines/${c.disciplineId}`, { method: "PUT", body: { code } }),
    onSuccess: () => { queryClient.invalidateQueries({ queryKey: ["record-numbers"] }); toast(`${c.discipline} saved`); },
    onError: (e) => toast(e.message, "error"),
  });
  return (
    <tr className="border-b border-line last:border-0">
      <td className="py-2 pr-3 font-semibold text-ink">{c.discipline}</td>
      <td className="py-2 pr-3 text-muted">{registerLabel[c.register] ?? c.register}</td>
      <td className="py-2 pr-3"><TextInput value={code} className="w-28 uppercase" maxLength={10} placeholder="By hand" onChange={(e) => setCode(e.target.value.toUpperCase())} /></td>
      <td className="py-2 text-right"><Button variant="secondary" busy={save.isPending} disabled={code.trim() === (c.code ?? "")} onClick={() => save.mutate()}>Save</Button></td>
    </tr>
  );
}

function SettingRow({ s }: { s: GateFormUnitSetting }) {
  const toast = useToast();
  const queryClient = useQueryClient();
  const initial = { mechanicalSource: s.mechanicalSource ?? "", electricalSource: s.electricalSource ?? "", defaultUnitId: s.defaultUnitId ?? "" };
  const [v, setV] = useState(initial);
  useEffect(() => setV({ mechanicalSource: s.mechanicalSource ?? "", electricalSource: s.electricalSource ?? "", defaultUnitId: s.defaultUnitId ?? "" }), [s]);
  const dirty = v.mechanicalSource !== initial.mechanicalSource || v.electricalSource !== initial.electricalSource || v.defaultUnitId !== initial.defaultUnitId;
  const save = useMutation({
    mutationFn: () => api<GateFormUnitSetting>(`/api/gate-forms/settings/${s.unitId}`, { method: "PUT", body: v }),
    onSuccess: () => { queryClient.invalidateQueries({ queryKey: ["gate-form-settings"] }); toast(`${s.unitName} saved`); },
    onError: (e) => toast(e.message, "error"),
  });
  return (
    <tr className="border-b border-line last:border-0">
      <td className="px-4 py-2.5"><span className="font-semibold text-ink">{s.unitName}</span><span className="block text-sm text-muted">{s.plantName}</span></td>
      <td className="px-4 py-2.5"><TextInput value={v.mechanicalSource} placeholder="Not set" onChange={(e) => setV({ ...v, mechanicalSource: e.target.value })} /></td>
      <td className="px-4 py-2.5"><TextInput value={v.electricalSource} placeholder="Not set" onChange={(e) => setV({ ...v, electricalSource: e.target.value })} /></td>
      <td className="px-4 py-2.5"><TextInput value={v.defaultUnitId} className="w-28" placeholder="—" title="Printed on forms with a Unit ID field when the asset has none of its own" onChange={(e) => setV({ ...v, defaultUnitId: e.target.value })} /></td>
      <td className="px-4 py-2.5 text-right"><Button variant="secondary" busy={save.isPending} disabled={!dirty} onClick={() => save.mutate()}>Save</Button></td>
    </tr>
  );
}

function TemplateSlot({ t }: { t: GateFormTemplateSlot }) {
  const input = useRef<HTMLInputElement>(null);
  const toast = useToast();
  const queryClient = useQueryClient();
  const [notes, setNotes] = useState<string[]>([]);
  const base = `/api/gate-forms/templates/${t.plantId}/${t.kind}`;
  const refresh = () => queryClient.invalidateQueries({ queryKey: ["gate-form-templates"] });
  const fail = (e: Error) => toast(e.message, "error");

  const send = useMutation({
    mutationFn: (file: File) => upload<{ version: GateFormTemplateVersion; notes: string[] }>(base, file),
    onSuccess: (r) => { refresh(); setNotes(r.notes); toast(`${r.version.fileName} checked and kept. Download its sample, then activate it.`); },
    onError: fail,
  });
  const action = useMutation({
    mutationFn: ({ path, method = "POST" }: { path: string; method?: string }) => api<void>(`${base}${path}`, { method }),
    onSuccess: () => { refresh(); setNotes([]); },
    onError: fail,
  });
  const get = (path: string) => download(`${base}${path}`).catch(fail);

  return (
    <div className="rounded-md border border-line p-4">
      <div className="flex flex-wrap items-start gap-3">
        <div className="min-w-0 flex-1">
          <h3 className="font-semibold text-ink">{t.plantName} <span className="font-normal text-muted">· units {t.units}</span></h3>
          <p className="text-sm text-muted">{kindLabel[t.kind]}</p>
          <p className="mt-1">
            In use: <button type="button" className="font-semibold text-violet hover:underline" onClick={() => get("/file")}>{t.fileName}</button>
            {t.isBuiltIn && <span className="text-muted"> (built-in)</span>}
            <span className="block text-sm text-muted">Fields: {t.fields.join(", ")}</span>
          </p>
        </div>
        <Button variant="secondary" onClick={() => get("/sample")}>Sample</Button>
        <Button variant="secondary" busy={send.isPending} onClick={() => input.current?.click()}>Upload new version</Button>
        {!t.isBuiltIn && (
          <Button variant="secondary" disabled={action.isPending} onClick={() => window.confirm("Go back to the built-in template? The uploaded versions stay in the list.") && action.mutate({ path: "/built-in" })}>
            Use built-in
          </Button>
        )}
        <input ref={input} type="file" accept=".docx" className="hidden" onChange={(e) => { const f = e.target.files?.[0]; if (f) send.mutate(f); e.target.value = ""; }} />
      </div>
      {notes.length > 0 && (
        <ul className="mt-3 list-disc space-y-1 pl-5 text-sm text-ongoing">{notes.map((n) => <li key={n}>{n}</li>)}</ul>
      )}
      {t.versions.length > 0 && (
        <table className="mt-3 w-full text-left text-sm">
          <tbody>
            {t.versions.map((v) => (
              <tr key={v.id} className="border-t border-line">
                <td className="py-2 pr-3">
                  <button type="button" className="font-semibold text-violet hover:underline" onClick={() => get(`/file?version=${v.id}`)}>{v.fileName}</button>
                  {v.isActive && <span className="ml-2 rounded bg-violet px-1.5 py-0.5 text-xs font-semibold text-white">In use</span>}
                  <span className="block text-muted">{formatDateTime(v.uploadedAtUtc)} by {v.uploadedBy} · {v.fields.join(", ")}</span>
                </td>
                <td className="py-2 text-right whitespace-nowrap">
                  <Button variant="quiet" onClick={() => get(`/sample?version=${v.id}`)}>Sample</Button>
                  {!v.isActive && (
                    <>
                      <Button variant="quiet" disabled={action.isPending} onClick={() => action.mutate({ path: `/versions/${v.id}/activate` })}>Activate</Button>
                      <Button variant="quiet" disabled={action.isPending} onClick={() => window.confirm(`Delete ${v.fileName}?`) && action.mutate({ path: `/versions/${v.id}`, method: "DELETE" })}>Delete</Button>
                    </>
                  )}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </div>
  );
}
