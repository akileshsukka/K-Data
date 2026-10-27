import { useEffect, useState } from "react";
import { useParams } from "react-router";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api } from "../../api/client";
import type { CategoryRow } from "../../api/types";
import { useUnit } from "../../app/useCatalog";
import { Button } from "../../components/Button";
import { Field, SelectInput, TextInput } from "../../components/Field";
import { PageHeader } from "../../components/PageHeader";
import { useToast } from "../../components/Toast";

interface Draft {
  key: string;
  id: number | null;
  label: string;
  section: "Hold" | "NotApplicable";
  priority: number;
  keywords: string[];
  actionBy: string;
  emailAction: string;
  note: string;
  count?: number;
}

const toDraft = (c: CategoryRow): Draft => ({
  key: String(c.id), id: c.id, label: c.label, section: c.section, priority: c.priority, keywords: c.keywords,
  actionBy: c.actionBy ?? "", emailAction: c.emailAction ?? "", note: c.note ?? "", count: c.count,
});

/**
 * Dashboard categories of one unit. An equipment falls in the category whose keyword appears in its Preservation Remarks;
 * when several match, the lowest priority number wins. The app dashboard and every exported Excel use this list.
 */
export function CategoriesPage() {
  const unitId = Number(useParams().unitId);
  const { unit } = useUnit(unitId);
  const toast = useToast();
  const queryClient = useQueryClient();
  const categories = useQuery({ queryKey: ["categories", unitId], queryFn: ({ signal }) => api<CategoryRow[]>(`/api/units/${unitId}/categories`, { signal }) });
  const [drafts, setDrafts] = useState<Draft[]>([]);
  const [dirty, setDirty] = useState(false);

  useEffect(() => {
    if (categories.data && !dirty) setDrafts(categories.data.map(toDraft));
  }, [categories.data, dirty]);

  const save = useMutation({
    mutationFn: () => api<CategoryRow[]>(`/api/units/${unitId}/categories`, {
      method: "PUT",
      body: drafts.map((d) => ({ id: d.id, label: d.label, section: d.section, priority: d.priority, keywords: d.keywords, actionBy: d.actionBy || null, emailAction: d.emailAction || null, note: d.note || null })),
    }),
    onSuccess: (rows) => {
      queryClient.setQueryData(["categories", unitId], rows);
      queryClient.invalidateQueries({ queryKey: ["dashboard", unitId] });
      setDirty(false);
      setDrafts(rows.map(toDraft));
      toast("Categories saved. The dashboard and the next Excel use them.");
    },
    onError: (e) => toast(e.message, "error"),
  });

  function update(key: string, patch: Partial<Draft>) {
    setDirty(true);
    setDrafts((list) => list.map((d) => (d.key === key ? { ...d, ...patch } : d)));
  }
  function move(key: string, by: -1 | 1) {
    setDirty(true);
    setDrafts((list) => {
      const i = list.findIndex((d) => d.key === key);
      const j = i + by;
      if (j < 0 || j >= list.length) return list;
      const next = [...list];
      [next[i], next[j]] = [next[j], next[i]];
      return next;
    });
  }
  function add(section: Draft["section"]) {
    setDirty(true);
    const priority = Math.max(0, ...drafts.map((d) => d.priority)) + 1;
    setDrafts((list) => [...list, { key: `new-${Date.now()}`, id: null, label: "", section, priority, keywords: [], actionBy: "", emailAction: "", note: "" }]);
  }

  return (
    <div>
      <PageHeader
        title={`${unit?.name ?? "Unit"} dashboard categories`}
        description="Keywords are searched in the Preservation Remarks (Excel column S), ignoring case and extra spaces. When an equipment matches several, the lowest priority number wins."
        actions={
          <>
            {dirty && <Button variant="secondary" onClick={() => { setDirty(false); setDrafts((categories.data ?? []).map(toDraft)); }}>Discard changes</Button>}
            <Button busy={save.isPending} disabled={!dirty} onClick={() => save.mutate()}>Save categories</Button>
          </>
        }
      />

      {categories.isPending && <p className="mt-6 text-muted">Loading…</p>}
      {(["Hold", "NotApplicable"] as const).map((section) => {
        const list = drafts.filter((d) => d.section === section);
        return (
          <section key={section} className="mt-8">
            <div className="flex items-baseline justify-between gap-3">
              <h2 className="text-2xl font-semibold">{section === "Hold" ? "Hold categories" : "Not applicable categories"}</h2>
              <Button variant="quiet" onClick={() => add(section)}>Add category</Button>
            </div>
            {list.length === 0 && <p className="mt-2 text-muted">None.</p>}
            <ul className="mt-3 space-y-3">
              {list.map((d) => (
                <li key={d.key} className="rounded-lg border border-line bg-panel p-4">
                  <div className="grid gap-3 md:grid-cols-[1fr_9rem_7rem]">
                    <Field label="Category (as shown on the dashboard)">
                      <TextInput value={d.label} maxLength={200} onChange={(e) => update(d.key, { label: e.target.value })} placeholder="Cat 1: Under inspection" />
                    </Field>
                    <Field label="Section">
                      <SelectInput value={d.section} onChange={(e) => update(d.key, { section: e.target.value as Draft["section"] })}>
                        <option value="Hold">Hold</option>
                        <option value="NotApplicable">Not Applicable</option>
                      </SelectInput>
                    </Field>
                    <Field label="Priority">
                      <TextInput type="number" min={1} max={999} value={d.priority} onChange={(e) => update(d.key, { priority: Number(e.target.value) })} />
                    </Field>
                  </div>
                  <KeywordInput keywords={d.keywords} onChange={(keywords) => update(d.key, { keywords })} />
                  <div className="mt-3 grid gap-3 md:grid-cols-3">
                    <Field label="Action By"><TextInput value={d.actionBy} maxLength={200} onChange={(e) => update(d.key, { actionBy: e.target.value })} /></Field>
                    <Field label="Email action"><TextInput value={d.emailAction} maxLength={200} onChange={(e) => update(d.key, { emailAction: e.target.value })} /></Field>
                    <Field label="Note"><TextInput value={d.note} maxLength={1000} onChange={(e) => update(d.key, { note: e.target.value })} /></Field>
                  </div>
                  <div className="mt-3 flex flex-wrap items-center gap-2">
                    {d.count !== undefined && !dirty && <span className="mr-auto text-sm text-muted">{d.count} equipment in this category now</span>}
                    {(d.count === undefined || dirty) && <span className="mr-auto" />}
                    <Button variant="quiet" onClick={() => move(d.key, -1)} aria-label="Move up">Move up</Button>
                    <Button variant="quiet" onClick={() => move(d.key, 1)} aria-label="Move down">Move down</Button>
                    <Button variant="danger" onClick={() => { setDirty(true); setDrafts((l) => l.filter((x) => x.key !== d.key)); }}>Remove</Button>
                  </div>
                </li>
              ))}
            </ul>
          </section>
        );
      })}
    </div>
  );
}

function KeywordInput({ keywords, onChange }: { keywords: string[]; onChange: (k: string[]) => void }) {
  const [text, setText] = useState("");
  function commit() {
    const k = text.trim().replace(/\s+/g, " ");
    if (k && !keywords.some((x) => x.toLowerCase() === k.toLowerCase())) onChange([...keywords, k]);
    setText("");
  }
  return (
    <div className="mt-3">
      <span className="text-sm font-semibold text-ink">Keywords</span>
      <div className="mt-1 flex min-h-10 flex-wrap items-center gap-1.5 rounded-md border border-line bg-panel px-2 py-1.5 focus-within:border-violet">
        {keywords.map((k) => (
          <span key={k} className="inline-flex items-center gap-1 rounded bg-violet-soft px-2 py-0.5 text-sm font-semibold text-violet">
            {k}
            <button type="button" aria-label={`Remove ${k}`} onClick={() => onChange(keywords.filter((x) => x !== k))} className="text-violet/70 hover:text-violet">×</button>
          </span>
        ))}
        <input
          value={text}
          onChange={(e) => setText(e.target.value)}
          onKeyDown={(e) => { if (e.key === "Enter" || e.key === ",") { e.preventDefault(); commit(); } }}
          onBlur={commit}
          placeholder={keywords.length ? "Add another…" : "Type a keyword and press Enter"}
          className="min-w-40 flex-1 bg-transparent px-1 text-[15px] outline-none"
          aria-label="New keyword"
        />
      </div>
    </div>
  );
}
