import { useEffect, useState } from "react";
import { useParams } from "react-router";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api } from "../../api/client";
import type { CategoryRow, CategorySection } from "../../api/types";
import { useUnit } from "../../app/useCatalog";
import { Button } from "../../components/Button";
import { Field, SelectInput, TextInput } from "../../components/Field";
import { PageHeader } from "../../components/PageHeader";
import { useToast } from "../../components/Toast";

interface Draft {
  key: string;
  id: number | null;
  code: string | null;
  label: string;
  section: CategorySection;
  /** Section when loaded: a category with equipment cannot change it. */
  savedSection: CategorySection | null;
  priority: number;
  keywords: string[];
  actionBy: string;
  emailAction: string;
  recommendation: string;
  note: string;
  isActive: boolean;
  count: number;
}

const toDraft = (c: CategoryRow): Draft => ({
  key: String(c.id), id: c.id, code: c.code, label: c.label, section: c.section, savedSection: c.section, priority: c.priority,
  keywords: c.keywords, actionBy: c.actionBy ?? "", emailAction: c.emailAction ?? "", recommendation: c.recommendation ?? "",
  note: c.note ?? "", isActive: c.isActive, count: c.count,
});

const sectionTitle: Record<CategorySection, string> = { Hold: "Hold categories", NotApplicable: "Not applicable categories" };

/**
 * Dashboard categories of one unit. The engineer assigns each equipment to one category by its code (Cat1, Cat2…),
 * from the equipment page or the Excel. Codes are given by the app and never reused. Categories are deactivated,
 * never deleted, and only when no equipment has them. The four note columns go to the Excel dashboard.
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
      body: drafts.map((d) => ({
        id: d.id, label: d.label, section: d.section, priority: d.priority, keywords: d.keywords,
        actionBy: d.actionBy || null, emailAction: d.emailAction || null, recommendation: d.recommendation || null, note: d.note || null,
        isActive: d.isActive,
      })),
    }),
    onSuccess: (rows) => {
      queryClient.setQueryData(["categories", unitId], rows);
      queryClient.invalidateQueries({ queryKey: ["dashboard", unitId] });
      queryClient.invalidateQueries({ queryKey: ["category-options", unitId] });
      setDirty(false);
      setDrafts(rows.map(toDraft));
      toast("Categories saved. The dashboard, the equipment pages and the next Excel use them.");
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
      const d = list.find((x) => x.key === key)!;
      const same = list.filter((x) => x.section === d.section && x.isActive);
      const i = same.indexOf(d);
      const other = same[i + by];
      if (!other) return list;
      const next = [...list];
      const a = next.indexOf(d);
      const b = next.indexOf(other);
      [next[a], next[b]] = [next[b], next[a]];
      return next;
    });
  }
  function add(section: CategorySection) {
    setDirty(true);
    const priority = Math.max(0, ...drafts.map((d) => d.priority)) + 1;
    setDrafts((list) => [...list, {
      key: `new-${Date.now()}`, id: null, code: null, label: "", section, savedSection: null, priority, keywords: [],
      actionBy: "", emailAction: "", recommendation: "", note: "", isActive: true, count: 0,
    }]);
  }
  function deactivate(d: Draft) {
    if (d.id === null) {
      setDirty(true);
      setDrafts((l) => l.filter((x) => x.key !== d.key));
      return;
    }
    update(d.key, { isActive: false });
  }

  const inactive = drafts.filter((d) => !d.isActive);

  return (
    <div>
      <PageHeader
        title={`${unit?.name ?? "Unit"} dashboard categories`}
        description="Each equipment on Hold or Not Applicable gets one category, chosen by its code on the equipment page or in the Excel. The code is given by the app when you save and never changes."
        actions={
          <>
            {dirty && <Button variant="secondary" onClick={() => { setDirty(false); setDrafts((categories.data ?? []).map(toDraft)); }}>Discard changes</Button>}
            <Button busy={save.isPending} disabled={!dirty} onClick={() => save.mutate()}>Save categories</Button>
          </>
        }
      />

      {categories.isPending && <p className="mt-6 text-muted">Loading…</p>}
      {categories.isError && <p className="mt-6 text-hold">{categories.error.message}</p>}
      {(["Hold", "NotApplicable"] as const).map((section) => {
        const list = drafts.filter((d) => d.section === section && d.isActive);
        return (
          <section key={section} className="mt-8">
            <div className="flex items-baseline justify-between gap-3">
              <h2 className="text-2xl font-semibold">{sectionTitle[section]}</h2>
              <Button variant="quiet" onClick={() => add(section)}>Add category</Button>
            </div>
            {list.length === 0 && <p className="mt-2 text-muted">None.</p>}
            <ul className="mt-3 space-y-3">
              {list.map((d, i) => (
                <CategoryCard
                  key={d.key}
                  draft={d}
                  first={i === 0}
                  last={i === list.length - 1}
                  dirty={dirty}
                  onChange={(patch) => update(d.key, patch)}
                  onMove={(by) => move(d.key, by)}
                  onDeactivate={() => deactivate(d)}
                />
              ))}
            </ul>
          </section>
        );
      })}

      {inactive.length > 0 && (
        <section className="mt-10">
          <h2 className="text-xl font-semibold">Inactive categories</h2>
          <p className="mt-1 text-sm text-muted">They keep their code, so it is never given to another category. Reactivate one to use it again.</p>
          <ul className="mt-3 divide-y divide-line rounded-lg border border-line bg-panel">
            {inactive.map((d) => (
              <li key={d.key} className="flex flex-wrap items-center gap-3 px-4 py-3">
                <span className="w-14 font-semibold text-muted">{d.code ?? "—"}</span>
                <span className="flex-1">{d.label} <span className="text-sm text-muted">· {d.section === "Hold" ? "Hold" : "Not Applicable"}</span></span>
                <Button variant="secondary" onClick={() => update(d.key, { isActive: true })}>Reactivate</Button>
              </li>
            ))}
          </ul>
        </section>
      )}
    </div>
  );
}

function CategoryCard({ draft: d, first, last, dirty, onChange, onMove, onDeactivate }: {
  draft: Draft;
  first: boolean;
  last: boolean;
  dirty: boolean;
  onChange: (patch: Partial<Draft>) => void;
  onMove: (by: -1 | 1) => void;
  onDeactivate: () => void;
}) {
  const [showKeywords, setShowKeywords] = useState(false);
  const locked = d.count > 0;   // has equipment: cannot change section nor be deactivated
  return (
    <li className="rounded-lg border border-line bg-panel p-4">
      <div className="grid gap-3 md:grid-cols-[6rem_1fr_11rem]">
        <Field label="Code">
          <span className="mt-1 flex min-h-10 items-center rounded-md border border-line bg-surface px-3 text-[15px] font-semibold text-hold">
            {d.code ?? <span className="font-normal text-muted">on save</span>}
          </span>
        </Field>
        <Field label="Description (as shown on the dashboard)">
          <TextInput value={d.label} maxLength={200} onChange={(e) => onChange({ label: e.target.value })} placeholder="Under inspection" />
        </Field>
        <Field label="Section" hint={locked ? "Has equipment: cannot move" : undefined}>
          <SelectInput value={d.section} disabled={locked} onChange={(e) => onChange({ section: e.target.value as CategorySection })}>
            <option value="Hold">Hold</option>
            <option value="NotApplicable">Not Applicable</option>
          </SelectInput>
        </Field>
      </div>
      {d.id !== null && d.savedSection !== null && d.section !== d.savedSection && (
        <p className="mt-2 text-sm text-ongoing">Moving it to the other section gives it a new code there when you save.</p>
      )}
      <div className="mt-3 grid gap-3 md:grid-cols-2">
        <Field label="Action By"><TextInput value={d.actionBy} maxLength={200} onChange={(e) => onChange({ actionBy: e.target.value })} /></Field>
        <Field label="Email Action"><TextInput value={d.emailAction} maxLength={200} onChange={(e) => onChange({ emailAction: e.target.value })} /></Field>
        <Field label="EnerMech Recommendation"><TextInput value={d.recommendation} maxLength={1000} onChange={(e) => onChange({ recommendation: e.target.value })} /></Field>
        <Field label="Note"><TextInput value={d.note} maxLength={1000} onChange={(e) => onChange({ note: e.target.value })} /></Field>
      </div>

      <div className="mt-3">
        <button type="button" className="text-sm font-semibold text-violet" aria-expanded={showKeywords} onClick={() => setShowKeywords(!showKeywords)}>
          {showKeywords ? "Hide" : "Show"} keywords for the current Excel ({d.keywords.length})
        </button>
        {showKeywords && (
          <div className="mt-2 rounded-md border border-dashed border-line p-3">
            <p className="text-sm text-muted">
              Only used until the unit moves to the new Excel template: the current file still finds the category by these words in the
              Preservation Remarks (column S), lowest priority first.
            </p>
            <div className="mt-2 grid gap-3 md:grid-cols-[1fr_8rem]">
              <KeywordInput keywords={d.keywords} onChange={(keywords) => onChange({ keywords })} />
              <Field label="Priority">
                <TextInput type="number" min={1} max={999} value={d.priority} onChange={(e) => onChange({ priority: Number(e.target.value) })} />
              </Field>
            </div>
          </div>
        )}
      </div>

      <div className="mt-3 flex flex-wrap items-center gap-2">
        <span className="mr-auto text-sm text-muted">
          {d.id === null ? "New: gets its code when you save" : !dirty || d.count > 0 ? `${d.count} equipment in this category now` : ""}
        </span>
        <Button variant="quiet" className="disabled:opacity-40" disabled={first} onClick={() => onMove(-1)} aria-label="Move up">Move up</Button>
        <Button variant="quiet" className="disabled:opacity-40" disabled={last} onClick={() => onMove(1)} aria-label="Move down">Move down</Button>
        <Button
          variant="danger"
          className="disabled:opacity-40 disabled:hover:bg-panel disabled:hover:text-hold"
          disabled={locked}
          title={locked ? "Move its equipment to another category first" : undefined}
          onClick={onDeactivate}
        >
          {d.id === null ? "Remove" : "Deactivate"}
        </Button>
      </div>
    </li>
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
    <div>
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
