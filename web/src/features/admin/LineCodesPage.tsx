import { useState, type FormEvent } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { api } from "../../api/client";
import type { LineCode, LineCodeKind } from "../../api/types";
import { useLineCodes } from "../../app/useCatalog";
import { Button } from "../../components/Button";
import { TextInput } from "../../components/Field";
import { PageHeader } from "../../components/PageHeader";
import { useToast } from "../../components/Toast";
import { AdminTabs } from "./AdminTabs";

const kinds: { key: LineCodeKind; label: string; hint: string }[] = [
  { key: "Service", label: "Service designation", hint: "Third part of the line number: 39-0218-LS-16\"-61055-P → LS." },
];

/** Meanings of the codes in the line numbers (client line numbering standard). Codes are deactivated, never deleted. */
export function LineCodesPage() {
  const book = useLineCodes();
  const [kind, setKind] = useState<LineCodeKind>("Service");
  const [search, setSearch] = useState("");
  const current = kinds.find((k) => k.key === kind)!;
  const q = search.trim().toLowerCase();
  const rows = book.all.filter((c) => c.kind === kind && (!q || `${c.code} ${c.description}`.toLowerCase().includes(q)));

  return (
    <div>
      <AdminTabs />
      <PageHeader title="Line codes" description="Service designations of the line numbers (client line numbering standard). The registers and the line checks show these meanings; a code not in the list is flagged, never guessed." />
      <div className={`mt-5 flex-wrap gap-2 ${kinds.length > 1 ? "flex" : "hidden"}`} role="tablist">
        {kinds.map((k) => (
          <button key={k.key} type="button" role="tab" aria-selected={kind === k.key} onClick={() => setKind(k.key)}
            className={`min-h-9 rounded-full border px-3 text-sm font-semibold ${kind === k.key ? "border-violet bg-violet text-white" : "border-line bg-panel text-ink hover:border-violet"}`}>
            {k.label} <span className="tabular-nums opacity-80">{book.all.filter((c) => c.kind === k.key).length}</span>
          </button>
        ))}
      </div>
      <p className="mt-3 text-sm text-muted">{current.hint}</p>

      <AddCode kind={kind} />

      <div className="mt-5 max-w-3xl">
        <TextInput type="search" placeholder="Search code or meaning" value={search} onChange={(e) => setSearch(e.target.value)} aria-label="Search" className="mt-0" />
        <ul className="mt-3 divide-y divide-line rounded-lg border border-line bg-panel">
          {rows.map((c) => <CodeRow key={c.id} code={c} />)}
          {rows.length === 0 && <li className="px-4 py-3 text-muted">Nothing here.</li>}
        </ul>
      </div>
    </div>
  );
}

function useSave() {
  const client = useQueryClient();
  const toast = useToast();
  return useMutation({
    mutationFn: (c: { id?: number; kind: LineCodeKind; code: string; description: string; isActive: boolean }) =>
      api<LineCode>(c.id ? `/api/line-codes/${c.id}` : "/api/line-codes", { method: c.id ? "PUT" : "POST", body: c }),
    onSuccess: (c) => { client.invalidateQueries({ queryKey: ["catalog", "line-codes"] }); toast(`${c.code} saved`); },
    onError: (e) => toast(e.message, "error"),
  });
}

function AddCode({ kind }: { kind: LineCodeKind }) {
  const save = useSave();
  const [code, setCode] = useState("");
  const [description, setDescription] = useState("");
  function submit(e: FormEvent) {
    e.preventDefault();
    save.mutate({ kind, code, description, isActive: true }, { onSuccess: () => { setCode(""); setDescription(""); } });
  }
  return (
    <form onSubmit={submit} className="mt-4 flex max-w-3xl flex-wrap items-end gap-2">
      <label className="w-28">
        <span className="text-sm font-semibold text-ink">Code</span>
        <TextInput required maxLength={10} value={code} onChange={(e) => setCode(e.target.value.toUpperCase())} className="font-mono" />
      </label>
      <label className="min-w-[14rem] flex-1">
        <span className="text-sm font-semibold text-ink">Meaning</span>
        <TextInput required maxLength={120} value={description} onChange={(e) => setDescription(e.target.value)} />
      </label>
      <Button type="submit" busy={save.isPending}>Add code</Button>
    </form>
  );
}

function CodeRow({ code }: { code: LineCode }) {
  const save = useSave();
  const [editing, setEditing] = useState(false);
  const [text, setText] = useState(code.description);
  return (
    <li className={`flex flex-wrap items-center gap-x-4 gap-y-2 px-4 py-2.5 ${code.isActive ? "" : "opacity-60"}`}>
      <span className="w-16 font-mono font-semibold text-ink">{code.code}</span>
      {editing ? (
        <form className="flex min-w-0 flex-1 gap-2" onSubmit={(e) => { e.preventDefault(); save.mutate({ ...code, description: text }, { onSuccess: () => setEditing(false) }); }}>
          <TextInput autoFocus maxLength={120} value={text} onChange={(e) => setText(e.target.value)} className="mt-0" aria-label={`Meaning of ${code.code}`} />
          <Button type="submit" busy={save.isPending}>Save</Button>
          <Button type="button" variant="secondary" onClick={() => { setText(code.description); setEditing(false); }}>Cancel</Button>
        </form>
      ) : (
        <>
          <span className="min-w-0 flex-1">{code.description}{!code.isActive && <span className="ml-2 text-sm text-muted">(inactive)</span>}</span>
          <Button variant="quiet" onClick={() => setEditing(true)}>Edit</Button>
          <Button variant="quiet" busy={save.isPending} onClick={() => save.mutate({ ...code, isActive: !code.isActive })}>{code.isActive ? "Deactivate" : "Activate"}</Button>
        </>
      )}
    </li>
  );
}
