import { useEffect, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api } from "../../api/client";
import { Button } from "../../components/Button";
import { TextInput } from "../../components/Field";
import { PageHeader } from "../../components/PageHeader";
import { useToast } from "../../components/Toast";
import { AdminTabs } from "./AdminTabs";

interface OptionValue { value: string; isActive: boolean }
interface OptionSetAdmin { key: string; label: string; locked: boolean; values: OptionValue[] }

/** Dropdown lists: add values, reorder them, deactivate the ones no longer used. Values are never renamed or deleted. */
export function ListsPage() {
  const sets = useQuery({ queryKey: ["option-sets"], queryFn: ({ signal }) => api<OptionSetAdmin[]>("/api/option-sets", { signal }) });
  const [key, setKey] = useState<string>();
  const current = sets.data?.find((s) => s.key === key) ?? sets.data?.[0];

  return (
    <div>
      <AdminTabs />
      <PageHeader title="Lists" description="Values of the dropdowns (Current Activity, Action By, Priority…). A value no longer used is deactivated: assets that have it keep it, but it is no longer offered. Values are never renamed or deleted." />
      <div className="mt-6 grid gap-6 lg:grid-cols-[16rem_minmax(0,1fr)]">
        <nav aria-label="Lists" className="flex flex-wrap gap-2 lg:flex-col lg:gap-0.5">
          {(sets.data ?? []).map((s) => (
            <button key={s.key} type="button" onClick={() => setKey(s.key)} aria-current={current?.key === s.key}
              className={`rounded-md px-3 py-2 text-left font-medium ${current?.key === s.key ? "bg-violet text-white" : "border border-line bg-panel text-ink hover:border-violet lg:border-0 lg:bg-transparent"}`}>
              {s.label}
            </button>
          ))}
        </nav>
        {current && <ListEditor key={current.key} set={current} />}
      </div>
    </div>
  );
}

function ListEditor({ set }: { set: OptionSetAdmin }) {
  const toast = useToast();
  const client = useQueryClient();
  const [values, setValues] = useState<OptionValue[]>(set.values);
  const [added, setAdded] = useState("");
  useEffect(() => setValues(set.values), [set]);
  const dirty = JSON.stringify(values) !== JSON.stringify(set.values);
  const save = useMutation({
    mutationFn: () => api<OptionSetAdmin>(`/api/option-sets/${set.key}`, { method: "PUT", body: { values } }),
    onSuccess: () => {
      client.invalidateQueries({ queryKey: ["option-sets"] });
      client.invalidateQueries({ queryKey: ["catalog", "options"] });
      toast(`${set.label} saved`);
    },
    onError: (e) => toast(e.message, "error"),
  });
  const move = (i: number, d: -1 | 1) => {
    const next = [...values];
    [next[i], next[i + d]] = [next[i + d], next[i]];
    setValues(next);
  };
  function add() {
    const v = added.trim();
    if (!v) return;
    if (values.some((x) => x.value.toLowerCase() === v.toLowerCase())) { toast(`${v} is already in the list`, "error"); return; }
    setValues([...values, { value: v, isActive: true }]);
    setAdded("");
  }

  return (
    <section className="rounded-lg border border-line bg-panel">
      <div className="flex flex-wrap items-center justify-between gap-2 border-b border-line px-4 py-3">
        <h2 className="text-lg font-semibold">{set.label}</h2>
        {!set.locked && <Button busy={save.isPending} disabled={!dirty} onClick={() => save.mutate()}>Save changes</Button>}
      </div>
      {set.locked && <p className="border-b border-line bg-surface px-4 py-2 text-sm text-muted">This list is fixed: the gate rules depend on its values.</p>}
      <ul>
        {values.map((v, i) => (
          <li key={v.value} className={`flex items-center gap-2 border-b border-line px-4 py-2 ${v.isActive ? "" : "text-muted"}`}>
            <span className="min-w-0 flex-1 break-words">{v.value}{!v.isActive && <span className="ml-2 text-sm">(inactive)</span>}</span>
            {!set.locked && (
              <>
                <Button variant="quiet" disabled={i === 0} onClick={() => move(i, -1)} aria-label={`Move ${v.value} up`}>↑</Button>
                <Button variant="quiet" disabled={i === values.length - 1} onClick={() => move(i, 1)} aria-label={`Move ${v.value} down`}>↓</Button>
                <Button variant="quiet" onClick={() => setValues(values.map((x, j) => (j === i ? { ...x, isActive: !x.isActive } : x)))}>{v.isActive ? "Deactivate" : "Activate"}</Button>
              </>
            )}
          </li>
        ))}
      </ul>
      {!set.locked && (
        <form className="flex flex-wrap gap-2 p-4" onSubmit={(e) => { e.preventDefault(); add(); }}>
          <TextInput placeholder="New value, e.g. Purging" maxLength={200} value={added} onChange={(e) => setAdded(e.target.value)} className="mt-0 max-w-sm" aria-label="New value" />
          <Button type="submit" variant="secondary">Add to the list</Button>
          {dirty && <span className="self-center text-sm text-ongoing">Not saved yet</span>}
        </form>
      )}
    </section>
  );
}
