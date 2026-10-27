import { useState, type FormEvent } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { api, upload } from "../../api/client";
import type { PipeSpec, PipeSpecImportResult } from "../../api/types";
import { usePipeSpecs } from "../../app/useCatalog";
import { Button } from "../../components/Button";
import { TextInput } from "../../components/Field";
import { PageHeader } from "../../components/PageHeader";
import { useToast } from "../../components/Toast";
import { FileDrop } from "../excel/FileDrop";
import { AdminTabs } from "./AdminTabs";

type Draft = { code: string; pressureClass: string; flangeType: string; gasket: string; description: string; isActive: boolean };
const toDraft = (s?: PipeSpec): Draft => ({
  code: s?.code ?? "", pressureClass: s?.pressureClass ?? "", flangeType: s?.flangeType ?? "", gasket: s?.gasket ?? "", description: s?.description ?? "", isActive: s?.isActive ?? true,
});

/** Pipe spec catalogue: every line takes its class and flange type from the spec in its line number. */
export function PipeSpecsPage() {
  const specs = usePipeSpecs();
  const [search, setSearch] = useState("");
  const q = search.trim().toLowerCase();
  const rows = specs.all.filter((s) => !q || `${s.code} ${s.pressureClass ?? ""} ${s.flangeType ?? ""} ${s.gasket ?? ""} ${s.description ?? ""}`.toLowerCase().includes(q));

  return (
    <div>
      <AdminTabs />
      <PageHeader title="Pipe specs" description="Rating, flange face and gasket of each pipe class. Lines show them automatically from the spec in their line number; correcting a pipe class here updates all its lines. RFD and RFC are kept as RF: the gasket tells them apart." />
      <div className="mt-6 grid gap-6 xl:grid-cols-[minmax(0,1fr)_24rem]">
        <section>
          <SpecForm />
          <TextInput type="search" placeholder="Search pipe class, rating, face or gasket" value={search} onChange={(e) => setSearch(e.target.value)} aria-label="Search" className="mt-4" />
          <div className="mt-3 overflow-x-auto rounded-lg border border-line bg-panel">
            <table className="w-full border-separate border-spacing-0 text-left text-[15px]">
              <thead className="font-display text-muted">
                <tr>
                  {["Pipe class", "Rating", "Face", "Gasket", "Description", "Lines", ""].map((h) => <th key={h} className="border-b border-line px-3 py-2.5 font-semibold">{h}</th>)}
                </tr>
              </thead>
              <tbody>
                {rows.map((s) => <SpecRow key={s.id} spec={s} />)}
                {rows.length === 0 && <tr><td colSpan={7} className="px-3 py-4 text-muted">{specs.isPending ? "Loading…" : specs.all.length === 0 ? "No pipe classes yet. Add one or load the Excel." : "Nothing matches."}</td></tr>}
              </tbody>
            </table>
          </div>
        </section>
        <ImportCard />
      </div>
    </div>
  );
}

function useSave() {
  const client = useQueryClient();
  const toast = useToast();
  return useMutation({
    mutationFn: ({ id, d }: { id?: number; d: Draft }) => api<PipeSpec>(id ? `/api/pipe-specs/${id}` : "/api/pipe-specs", { method: id ? "PUT" : "POST", body: d }),
    onSuccess: (s) => {
      client.invalidateQueries({ queryKey: ["catalog", "pipe-specs"] });
      client.invalidateQueries({ queryKey: ["register"] });
      toast(`${s.code} saved`);
    },
    onError: (e) => toast(e.message, "error"),
  });
}

function SpecForm() {
  const save = useSave();
  const [d, setD] = useState<Draft>(toDraft());
  function submit(e: FormEvent) {
    e.preventDefault();
    save.mutate({ d }, { onSuccess: () => setD(toDraft()) });
  }
  return (
    <form onSubmit={submit} className="grid gap-2 rounded-lg border border-line bg-panel p-4 sm:grid-cols-[8rem_8rem_10rem_minmax(0,1fr)_auto] sm:items-end">
      <Labelled label="Pipe class"><TextInput required maxLength={20} placeholder="61055" value={d.code} onChange={(e) => setD({ ...d, code: e.target.value.toUpperCase() })} className="mt-1 font-mono" /></Labelled>
      <Labelled label="Rating"><TextInput maxLength={30} placeholder="150" value={d.pressureClass} onChange={(e) => setD({ ...d, pressureClass: e.target.value })} className="mt-1" /></Labelled>
      <Labelled label="Face"><TextInput maxLength={50} placeholder="RF, RTJ or FF" value={d.flangeType} onChange={(e) => setD({ ...d, flangeType: e.target.value })} className="mt-1" /></Labelled>
      <Labelled label="Gasket"><TextInput maxLength={100} placeholder="Graphite" value={d.gasket} onChange={(e) => setD({ ...d, gasket: e.target.value })} className="mt-1" /></Labelled>
      <Button type="submit" busy={save.isPending}>Add pipe class</Button>
    </form>
  );
}

function Labelled({ label, children }: { label: string; children: React.ReactNode }) {
  return <label className="block min-w-0"><span className="text-sm font-semibold text-ink">{label}</span>{children}</label>;
}

function SpecRow({ spec }: { spec: PipeSpec }) {
  const save = useSave();
  const [editing, setEditing] = useState(false);
  const [d, setD] = useState<Draft>(toDraft(spec));
  const cell = "border-b border-line px-3 py-2";
  if (editing) {
    const input = (k: keyof Draft, max: number) => (
      <TextInput maxLength={max} value={d[k] as string} onChange={(e) => setD({ ...d, [k]: e.target.value })} className="mt-0 h-9" aria-label={`${k} of ${spec.code}`} />
    );
    return (
      <tr className="bg-violet-soft/40">
        <td className={cell}>{input("code", 20)}</td>
        <td className={cell}>{input("pressureClass", 30)}</td>
        <td className={cell}>{input("flangeType", 50)}</td>
        <td className={cell}>{input("gasket", 100)}</td>
        <td className={cell}>{input("description", 200)}</td>
        <td className={`${cell} tabular-nums`}>{spec.lines}</td>
        <td className={`${cell} whitespace-nowrap`}>
          <Button variant="quiet" busy={save.isPending} onClick={() => save.mutate({ id: spec.id, d }, { onSuccess: () => setEditing(false) })}>Save</Button>
          <Button variant="quiet" onClick={() => { setD(toDraft(spec)); setEditing(false); }}>Cancel</Button>
        </td>
      </tr>
    );
  }
  return (
    <tr className={spec.isActive ? "" : "text-muted"}>
      <td className={`${cell} font-mono font-semibold text-ink`}>{spec.code}{!spec.isActive && <span className="ml-1 font-sans text-sm font-normal text-muted">(inactive)</span>}</td>
      <td className={cell}>{spec.pressureClass}</td>
      <td className={cell}>{spec.flangeType}</td>
      <td className={cell}>{spec.gasket}</td>
      <td className={`${cell} max-w-[16rem] text-sm`}>{spec.description}</td>
      <td className={`${cell} tabular-nums`}>{spec.lines}</td>
      <td className={`${cell} whitespace-nowrap`}>
        <Button variant="quiet" onClick={() => setEditing(true)}>Edit</Button>
        <Button variant="quiet" busy={save.isPending} onClick={() => save.mutate({ id: spec.id, d: { ...toDraft(spec), isActive: !spec.isActive } })}>{spec.isActive ? "Deactivate" : "Activate"}</Button>
      </td>
    </tr>
  );
}

/** Load the client's pipe spec Excel: preview first, then apply. New specs are added; known ones get the non-empty values. */
function ImportCard() {
  const toast = useToast();
  const client = useQueryClient();
  const [file, setFile] = useState<File>();
  const [result, setResult] = useState<PipeSpecImportResult & { applied: boolean }>();
  const run = useMutation({
    mutationFn: ({ f, apply }: { f: File; apply: boolean }) => upload<PipeSpecImportResult>(`/api/pipe-specs/import${apply ? "?apply=true" : ""}`, f).then((r) => ({ ...r, applied: apply })),
    onSuccess: (r) => {
      setResult(r);
      if (r.applied) {
        client.invalidateQueries({ queryKey: ["catalog", "pipe-specs"] });
        client.invalidateQueries({ queryKey: ["register"] });
        toast(`${r.new} added, ${r.updated} updated`);
      }
    },
    onError: (e) => toast(e.message, "error"),
  });
  const changes = (result?.rows ?? []).filter((r) => r.outcome !== "Unchanged");
  return (
    <section className="rounded-lg border border-line bg-panel p-4 xl:self-start">
      <h2 className="font-display text-lg font-semibold">Load from Excel</h2>
      <p className="mt-1 text-sm text-muted">A sheet with a <strong>Pipe Class</strong> (or Pipe Spec) column and a <strong>Rating</strong> and/or <strong>Face</strong> column, like the Unit 39 pipe class register. <strong>Gasket</strong> is read too. RFD / RFC become RF. Nothing is deleted.</p>
      <div className="mt-3"><FileDrop busy={run.isPending} label="Choose the pipe class register" onFile={(f) => { setFile(f); run.mutate({ f, apply: false }); }} /></div>
      {result && (
        <div className="mt-4" aria-live="polite">
          <p className="text-sm text-muted">Sheet "{result.sheet}" · columns read: {result.columns.join(", ")}</p>
          <p className="mt-1"><strong>{result.new}</strong> {result.applied ? "added" : "new"} · <strong>{result.updated}</strong> {result.applied ? "updated" : "to update"} · {result.unchanged} unchanged · <span className={result.invalid ? "text-hold" : ""}>{result.invalid} not read</span></p>
          {!result.applied && result.new + result.updated > 0 && file && (
            <Button className="mt-3" busy={run.isPending} onClick={() => run.mutate({ f: file, apply: true })}>Apply {result.new + result.updated} changes</Button>
          )}
          {changes.length > 0 && (
            <ul className="mt-3 max-h-96 divide-y divide-line overflow-auto rounded-md border border-line text-sm">
              {changes.map((r) => (
                <li key={r.row} className="px-3 py-2">
                  <span className="font-mono font-semibold">{r.code}</span> <span className="text-muted">row {r.row}</span>
                  <span className={`ml-2 font-semibold ${r.outcome === "Invalid" ? "text-hold" : "text-violet"}`}>{r.outcome}</span>
                  <span className="block">{r.outcome === "Invalid" ? r.reason : <>{r.before && <span className="text-muted">{r.before} → </span>}{[r.pressureClass, r.flangeType, r.gasket].filter(Boolean).join(" · ")}</>}</span>
                </li>
              ))}
            </ul>
          )}
        </div>
      )}
    </section>
  );
}
