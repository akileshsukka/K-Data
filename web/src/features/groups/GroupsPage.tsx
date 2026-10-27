import { useState, type FormEvent } from "react";
import { Link, useNavigate, useParams } from "react-router";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api } from "../../api/client";
import { Perm, type Group, type GroupDetail, type GroupType } from "../../api/types";
import { useUnit } from "../../app/useCatalog";
import { useSession } from "../../app/session";
import { Button } from "../../components/Button";
import { Dialog } from "../../components/Dialog";
import { Field, SelectInput, TextInput } from "../../components/Field";
import { PageHeader } from "../../components/PageHeader";
import { useToast } from "../../components/Toast";
import { GroupProgressBars } from "./GroupProgressBars";

export const useGroupTypes = () => useQuery({ queryKey: ["group-types"], queryFn: ({ signal }) => api<GroupType[]>("/api/group-types", { signal }), staleTime: Infinity });

/** Workpacks, circuits and systems of the unit: internal groups of equipment, lines and E&I items. */
export function GroupsPage() {
  const unitId = Number(useParams().unitId);
  const { unit } = useUnit(unitId);
  const session = useSession();
  const [inactive, setInactive] = useState(false);
  const [creating, setCreating] = useState(false);
  const [type, setType] = useState("");
  const groups = useQuery({
    queryKey: ["groups", unitId, inactive],
    queryFn: ({ signal }) => api<Group[]>(`/api/groups?unitId=${unitId}&includeInactive=${inactive}`, { signal }),
  });
  const types = useGroupTypes();
  const list = (groups.data ?? []).filter((g) => !type || g.type === type);

  return (
    <div>
      <PageHeader
        title="Workpacks and groups"
        description={unit ? `${unit.name} — group any equipment, lines and E&I items to follow them together. Groups are internal: they never go to the client Excel nor to Simpro.` : undefined}
        actions={session.can(Perm.groupsManage) ? <Button onClick={() => setCreating(true)}>New group</Button> : undefined}
      />
      <div className="mt-5 flex flex-wrap items-center gap-2">
        {["", ...(types.data ?? []).map((t) => t.name)].map((t) => (
          <button key={t || "all"} type="button" aria-pressed={type === t} onClick={() => setType(t)}
            className={`min-h-9 rounded-full border px-3 text-sm font-semibold ${type === t ? "border-violet bg-violet text-white" : "border-line bg-panel text-ink hover:border-violet"}`}>
            {t || "All"} <span className="tabular-nums opacity-80">{(groups.data ?? []).filter((g) => !t || g.type === t).length}</span>
          </button>
        ))}
        <label className="ml-auto inline-flex items-center gap-2 text-[15px]">
          <input type="checkbox" className="size-4 accent-violet" checked={inactive} onChange={(e) => setInactive(e.target.checked)} />
          Show inactive
        </label>
      </div>

      {groups.isPending && <p className="mt-6 text-muted">Loading…</p>}
      {groups.data && list.length === 0 && (
        <p className="mt-6 text-muted">No groups yet. {session.can(Perm.groupsManage) && "Create one, or select assets in a register and use \"Add to group\"."}</p>
      )}
      <ul className="mt-5 grid gap-3 lg:grid-cols-2">
        {list.map((g) => (
          <li key={g.id}>
            <Link to={`/groups/${g.id}`} className={`block rounded-lg border border-line bg-panel p-4 hover:border-violet ${g.isActive ? "" : "opacity-60"}`}>
              <div className="flex flex-wrap items-baseline justify-between gap-x-3">
                <span className="font-display text-lg font-semibold text-ink">{g.code}</span>
                <span className="text-sm text-muted">{g.type}{!g.isActive && " · inactive"}{g.unitId == null && " · all units"}</span>
              </div>
              <p className="text-ink">{g.name}</p>
              <p className="mt-1 text-sm text-muted">
                {g.progress.members} item{g.progress.members === 1 ? "" : "s"}
                {g.progress.members > 0 && ` · ${[g.progress.equipment && `${g.progress.equipment} equipment`, g.progress.lines && `${g.progress.lines} lines`, g.progress.electricalInstrument && `${g.progress.electricalInstrument} E&I`].filter(Boolean).join(", ")}`}
                {g.progress.preserved > 0 && ` · ${g.progress.preserved} preserved`}
                {g.progress.removedFromSite > 0 && ` · ${g.progress.removedFromSite} removed from site`}
              </p>
              {g.progress.members > 0 && <GroupProgressBars progress={g.progress} compact />}
            </Link>
          </li>
        ))}
      </ul>

      <GroupDialog open={creating} onClose={() => setCreating(false)} unitId={unitId} />
    </div>
  );
}

/** Create a group (or edit one when <paramref name="group"/> is given). The code is generated when left empty. */
export function GroupDialog({ open, onClose, unitId, group, onSaved }: { open: boolean; onClose: () => void; unitId: number | null; group?: Group; onSaved?: (d: GroupDetail) => void }) {
  const types = useGroupTypes();
  const navigate = useNavigate();
  const toast = useToast();
  const client = useQueryClient();
  const [form, setForm] = useState({ typeId: group?.typeId ?? 0, code: group?.code ?? "", name: group?.name ?? "", description: group?.description ?? "", isActive: group?.isActive ?? true });
  const typeId = form.typeId || types.data?.[0]?.id || 0;
  const prefix = types.data?.find((t) => t.id === typeId)?.prefix;
  const save = useMutation({
    mutationFn: () => api<GroupDetail>(group ? `/api/groups/${group.id}` : "/api/groups", {
      method: group ? "PUT" : "POST",
      body: { typeId, code: form.code.trim() || null, name: form.name, description: form.description.trim() || null, unitId: group ? group.unitId : unitId, isActive: form.isActive },
    }),
    onSuccess: (d) => {
      client.invalidateQueries({ queryKey: ["groups"] });
      client.setQueryData(["group", d.group.id], d);
      toast(`${d.group.code} saved`);
      onClose();
      if (onSaved) onSaved(d); else navigate(`/groups/${d.group.id}`);
    },
    onError: (e) => toast(e.message, "error"),
  });
  function submit(e: FormEvent) { e.preventDefault(); save.mutate(); }
  return (
    <Dialog open={open} title={group ? `Edit ${group.code}` : "New group"} onClose={onClose}>
      <form onSubmit={submit} className="space-y-4">
        <Field label="Kind">
          <SelectInput value={typeId} onChange={(e) => setForm({ ...form, typeId: Number(e.target.value) })}>
            {(types.data ?? []).map((t) => <option key={t.id} value={t.id}>{t.name}</option>)}
          </SelectInput>
        </Field>
        <Field label="Name" hint="e.g. Air coolers north, Flare header circuit">
          <TextInput required maxLength={200} value={form.name} onChange={(e) => setForm({ ...form, name: e.target.value })} />
        </Field>
        <Field label="Code" hint={group ? undefined : `Leave empty to get the next one (${prefix ?? "WP"}-…-001).`}>
          <TextInput maxLength={50} value={form.code} onChange={(e) => setForm({ ...form, code: e.target.value.toUpperCase() })} className="font-mono" />
        </Field>
        <Field label="Description">
          <TextInput maxLength={1000} value={form.description} onChange={(e) => setForm({ ...form, description: e.target.value })} />
        </Field>
        {group && (
          <label className="inline-flex items-center gap-2">
            <input type="checkbox" className="size-4 accent-violet" checked={form.isActive} onChange={(e) => setForm({ ...form, isActive: e.target.checked })} />
            Active (inactive groups are hidden and cannot receive assets)
          </label>
        )}
        <div className="flex justify-end gap-2">
          <Button type="button" variant="secondary" onClick={onClose}>Cancel</Button>
          <Button type="submit" busy={save.isPending}>{group ? "Save" : "Create"}</Button>
        </div>
      </form>
    </Dialog>
  );
}
