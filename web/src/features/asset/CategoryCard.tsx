import { useState } from "react";
import { Link } from "react-router";
import { useQuery } from "@tanstack/react-query";
import { api, ApiError } from "../../api/client";
import { Perm, type AssetProfile, type CategoryOption } from "../../api/types";
import { useSession } from "../../app/session";
import { Button } from "../../components/Button";
import { SelectInput } from "../../components/Field";
import { useToast } from "../../components/Toast";
import { useSetCategory } from "./useAsset";

/**
 * Hold / N/A dashboard category of the asset. One at most: the section comes from the category.
 * The same choice is made in the Excel with the Hold Category / N/A Category columns.
 */
export function CategoryCard({ asset }: { asset: AssetProfile }) {
  const session = useSession();
  const toast = useToast();
  const set = useSetCategory(asset.id);
  const options = useQuery({
    queryKey: ["category-options", asset.unit.id],
    queryFn: ({ signal }) => api<CategoryOption[]>(`/api/units/${asset.unit.id}/category-options`, { signal }),
  });
  const current = asset.dashboardCategory;
  const [choice, setChoice] = useState<string | null>(null);
  const canEdit = session.can(Perm.assetsEdit) && (asset.isActive || session.can(Perm.assetsManage));
  const value = choice ?? (current ? String(current.id) : "");
  const changed = choice !== null && choice !== (current ? String(current.id) : "");

  async function save() {
    try {
      await set.mutateAsync({ categoryId: value ? Number(value) : null, expected: current?.id ?? null });
      toast(value ? "Category saved" : "Category removed");
      setChoice(null);
    } catch (err) {
      toast(err instanceof ApiError ? err.message : "Could not save.", "error");
    }
  }

  const hold = (options.data ?? []).filter((o) => o.section === "Hold");
  const na = (options.data ?? []).filter((o) => o.section === "NotApplicable");
  const name = (o: { code: string | null; label: string }) => `${o.code ?? ""} – ${o.label}`;

  return (
    <section className="rounded-lg border border-line bg-panel p-4">
      <div className="flex items-baseline justify-between gap-3">
        <h2 className="text-lg font-semibold">Dashboard category</h2>
        {session.can(Perm.categoriesManage) && (
          <Link to={`/u/${asset.unit.id}/categories`} className="text-sm font-semibold text-violet">Edit categories</Link>
        )}
      </div>
      <p className="mt-1">
        {current ? (
          <>
            <span className={`mr-2 rounded px-1.5 py-0.5 text-sm font-semibold ${current.section === "Hold" ? "bg-hold/10 text-hold" : "bg-na/20 text-ink"}`}>
              {current.section === "Hold" ? "Hold" : "N/A"} {current.code}
            </span>
            {current.label}
          </>
        ) : (
          <span className="text-muted">None: counted as available for preservation.</span>
        )}
      </p>
      {canEdit && (
        <div className="mt-3 flex flex-wrap items-center gap-2">
          <SelectInput
            aria-label="Dashboard category"
            className="mt-0 w-auto min-w-64 flex-1"
            value={value}
            disabled={options.isPending}
            onChange={(e) => setChoice(e.target.value)}
          >
            <option value="">None</option>
            {hold.length > 0 && (
              <optgroup label="Hold">
                {hold.map((o) => <option key={o.id} value={o.id}>{name(o)}</option>)}
              </optgroup>
            )}
            {na.length > 0 && (
              <optgroup label="Not Applicable">
                {na.map((o) => <option key={o.id} value={o.id}>{name(o)}</option>)}
              </optgroup>
            )}
          </SelectInput>
          {changed && (
            <>
              <Button busy={set.isPending} onClick={save}>Save</Button>
              <Button variant="secondary" onClick={() => setChoice(null)}>Cancel</Button>
            </>
          )}
        </div>
      )}
      {options.data?.length === 0 && <p className="mt-2 text-sm text-muted">This unit has no categories yet.</p>}
    </section>
  );
}
