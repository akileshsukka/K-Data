import { useState } from "react";
import { Link, useNavigate, useParams } from "react-router";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api } from "../../api/client";
import { Perm, type Group, type GroupDetail, type MembersResult } from "../../api/types";
import { Dialog } from "../../components/Dialog";
import { SelectInput } from "../../components/Field";
import { lastUnit } from "../../app/useCatalog";
import { useSession } from "../../app/session";
import { AssetSearch } from "../../components/AssetSearch";
import { Button } from "../../components/Button";
import { CurrentGateChip, GateStatus } from "../../components/GateStatus";
import { PageHeader } from "../../components/PageHeader";
import { useToast } from "../../components/Toast";
import { formatDate } from "../../components/dates";
import { GroupProgressBars } from "./GroupProgressBars";
import { GroupDialog } from "./GroupsPage";

const typeLabel = { Equipment: "Equipment", Pipeline: "Line", ElectricalInstrument: "E&I" } as const;

/** One workpack or group: its progress by gate and its members (any type), with add and remove. */
export function GroupPage() {
  const id = Number(useParams().id);
  const session = useSession();
  const toast = useToast();
  const client = useQueryClient();
  const [editing, setEditing] = useState(false);
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [deleting, setDeleting] = useState(false);
  const [source, setSource] = useState("");
  const navigate = useNavigate();
  const detail = useQuery({ queryKey: ["group", id], queryFn: ({ signal }) => api<GroupDetail>(`/api/groups/${id}`, { signal }) });
  const canManage = session.can(Perm.groupsManage);

  const members = useMutation({
    mutationFn: ({ ids, remove }: { ids: string[]; remove: boolean }) =>
      api<MembersResult>(`/api/groups/${id}/members${remove ? "/remove" : ""}`, { method: "POST", body: { assetIds: ids } }),
    onSuccess: (r, v) => {
      client.setQueryData(["group", id], r.detail);
      client.invalidateQueries({ queryKey: ["groups"] });
      client.invalidateQueries({ queryKey: ["register"] });
      setSelected(new Set());
      toast(v.remove ? `${r.removed} removed from the group` : r.added > 0 ? `${r.added} added` : "Already in the group");
    },
    onError: (e) => toast(e.message, "error"),
  });

  const others = useQuery({
    queryKey: ["groups", detail.data?.group.unitId ?? "all", false],
    queryFn: ({ signal }) => api<Group[]>(`/api/groups?includeInactive=false${detail.data?.group.unitId ? `&unitId=${detail.data.group.unitId}` : ""}`, { signal }),
    enabled: canManage && !!detail.data,
  });
  const setActive = useMutation({
    mutationFn: (active: boolean) => api<GroupDetail>(`/api/groups/${id}/active?active=${active}`, { method: "POST" }),
    onSuccess: (d) => { client.setQueryData(["group", id], d); client.invalidateQueries({ queryKey: ["groups"] }); toast(d.group.isActive ? "Group activated" : "Group deactivated"); },
    onError: (e) => toast(e.message, "error"),
  });
  const remove = useMutation({
    mutationFn: () => api(`/api/groups/${id}`, { method: "DELETE" }),
    onSuccess: () => {
      client.invalidateQueries({ queryKey: ["groups"] });
      client.invalidateQueries({ queryKey: ["register"] });
      toast("Group deleted");
      const unit = detail.data?.group.unitId ?? lastUnit.get();
      navigate(unit ? `/u/${unit}/groups` : "/units");
    },
    onError: (e) => toast(e.message, "error"),
  });
  const copyFrom = useMutation({
    mutationFn: async (from: string) => {
      const d = await api<GroupDetail>(`/api/groups/${from}`);
      if (d.members.length === 0) throw new Error(`${d.group.code} has no items.`);
      return api<MembersResult>(`/api/groups/${id}/members`, { method: "POST", body: { assetIds: d.members.map((m) => m.id) } });
    },
    onSuccess: (r) => {
      client.setQueryData(["group", id], r.detail);
      client.invalidateQueries({ queryKey: ["groups"] });
      client.invalidateQueries({ queryKey: ["register"] });
      setSource("");
      toast(`${r.added} added${r.alreadyIn ? ` (${r.alreadyIn} already in it)` : ""}`);
    },
    onError: (e) => toast(e.message, "error"),
  });

  if (detail.isPending) return <p className="text-muted">Loading…</p>;
  if (detail.error) return <p className="text-hold">{detail.error.message}</p>;
  const { group: g, members: list } = detail.data;
  const unitForLinks = g.unitId ?? lastUnit.get();

  return (
    <div>
      {unitForLinks ? <Link to={`/u/${unitForLinks}/groups`} className="text-sm font-semibold text-violet">Workpacks and groups</Link> : null}
      <PageHeader
        title={`${g.code} — ${g.name}`}
        description={`${g.type} · ${g.unitName ?? "all units"}${g.isActive ? "" : " · inactive"}${g.description ? ` · ${g.description}` : ""}`}
        actions={
          <>
            {unitForLinks && <Link to={`/u/${unitForLinks}/monitoring?group=${g.id}`} className="inline-flex min-h-10 items-center rounded-md border border-line bg-panel px-4 font-semibold text-ink hover:border-violet hover:text-violet">Monitoring</Link>}
            {canManage && <Button variant="secondary" onClick={() => setEditing(true)}>Edit</Button>}
            {canManage && <Button variant="secondary" busy={setActive.isPending} onClick={() => setActive.mutate(!g.isActive)}>{g.isActive ? "Deactivate" : "Activate"}</Button>}
            {canManage && <Button variant="danger" onClick={() => setDeleting(true)}>Delete</Button>}
          </>
        }
      />

      <section className="mt-5 rounded-lg border border-line bg-panel p-4">
        <h2 className="font-display text-lg font-semibold">Progress</h2>
        <p className="text-sm text-muted">
          {g.progress.members} active item{g.progress.members === 1 ? "" : "s"}: {g.progress.equipment} equipment, {g.progress.lines} lines, {g.progress.electricalInstrument} E&I
          {" · "}{g.progress.preserved} preserved{g.progress.removedFromSite > 0 && ` · ${g.progress.removedFromSite} removed from site (not counted)`}
        </p>
        {g.progress.members > 0 ? <GroupProgressBars progress={g.progress} /> : <p className="mt-2 text-muted">No items yet.</p>}
      </section>

      {canManage && g.isActive && (
        <div className="mt-5 max-w-xl">
          <span className="text-sm font-semibold text-ink">Add an item</span>
          <AssetSearch unitId={g.unitId ?? undefined} placeholder="Find equipment, a line or an E&I item" excludeIds={new Set(list.map((m) => m.id))}
            onPick={(h) => members.mutate({ ids: [h.id], remove: false })} />
          <p className="mt-1 text-sm text-muted">To add many at once, select them in a register and use "Add to group".</p>
          <div className="mt-4 flex flex-wrap items-end gap-2">
            <label className="min-w-[16rem] flex-1">
              <span className="text-sm font-semibold text-ink">Add all the items of another group</span>
              <SelectInput value={source} onChange={(e) => setSource(e.target.value)} className="mt-1">
                <option value="">Choose a group (e.g. a circuit)</option>
                {(others.data ?? []).filter((o) => o.id !== g.id).map((o) => <option key={o.id} value={o.id}>{o.code} — {o.name} ({o.type}, {o.progress.members})</option>)}
              </SelectInput>
            </label>
            <Button variant="secondary" disabled={!source} busy={copyFrom.isPending} onClick={() => copyFrom.mutate(source)}>Add its items</Button>
          </div>
        </div>
      )}

      <div className="mt-5 flex flex-wrap items-center justify-between gap-2">
        <h2 className="font-display text-lg font-semibold">Items <span className="text-muted tabular-nums">{list.length}</span></h2>
        {canManage && selected.size > 0 && (
          <Button variant="danger" busy={members.isPending} onClick={() => members.mutate({ ids: [...selected], remove: true })}>Remove {selected.size} from the group</Button>
        )}
      </div>
      {list.length > 0 && (
        <div className="mt-2 overflow-x-auto rounded-lg border border-line bg-panel">
          <table className="w-full border-separate border-spacing-0 text-left text-[15px]">
            <thead className="font-display text-muted">
              <tr>
                {canManage && <th className="w-10 border-b border-line px-3 py-2.5"><span className="sr-only">Select</span></th>}
                <th className="border-b border-line px-3 py-2.5 font-semibold">Tag</th>
                <th className="border-b border-line px-3 py-2.5 font-semibold">Type</th>
                <th className="border-b border-line px-3 py-2.5 font-semibold">Current Gate</th>
                {[1, 2, 3, 4].map((n) => <th key={n} className="border-b border-line px-2 py-2.5 font-semibold">G{n}</th>)}
                <th className="border-b border-line px-3 py-2.5 font-semibold">Preserved</th>
                <th className="border-b border-line px-3 py-2.5 font-semibold">Added</th>
              </tr>
            </thead>
            <tbody>
              {list.map((m) => (
                <tr key={m.id} className={`group hover:bg-violet-soft/50 ${m.isActive ? "" : "text-muted"}`}>
                  {canManage && (
                    <td className="border-b border-line px-3 py-2">
                      <input type="checkbox" className="size-4 accent-violet" checked={selected.has(m.id)} aria-label={`Select ${m.tag ?? m.code}`}
                        onChange={() => { const n = new Set(selected); if (n.has(m.id)) n.delete(m.id); else n.add(m.id); setSelected(n); }} />
                    </td>
                  )}
                  <td className="border-b border-line px-3 py-2">
                    <Link to={`/assets/${m.id}`} className="font-semibold whitespace-nowrap text-ink group-hover:text-violet">{m.tag ?? m.code}</Link>
                    <span className="block text-sm text-muted">{m.code}{!m.isActive && " · inactive"}</span>
                  </td>
                  <td className="border-b border-line px-3 py-2 text-sm">{typeLabel[m.type]}<span className="block text-muted">{m.discipline}</span></td>
                  <td className="border-b border-line px-3 py-2"><CurrentGateChip value={m.currentGate} /></td>
                  {m.gates.slice(0, 4).map((s, i) => <td key={i} className="border-b border-line px-2 py-2"><GateStatus value={s} compact /></td>)}
                  <td className="border-b border-line px-3 py-2 whitespace-nowrap">{m.preservedAtUtc ? formatDate(m.preservedAtUtc) : "—"}</td>
                  <td className="border-b border-line px-3 py-2 text-sm whitespace-nowrap text-muted">{formatDate(m.addedAtUtc)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      <Dialog open={deleting} title={`Delete ${g.code}?`} onClose={() => setDeleting(false)}>
        <p>The group and its list of {list.length} item{list.length === 1 ? "" : "s"} are deleted. The equipment, lines and E&I items themselves are not changed.</p>
        <p className="mt-2 text-muted">To keep it for the record instead, deactivate it.</p>
        <div className="mt-5 flex justify-end gap-2">
          <Button variant="secondary" onClick={() => setDeleting(false)}>Cancel</Button>
          <Button variant="danger" busy={remove.isPending} onClick={() => remove.mutate()}>Delete group</Button>
        </div>
      </Dialog>

      {editing && <GroupDialog open={editing} onClose={() => setEditing(false)} unitId={g.unitId} group={g} onSaved={() => undefined} />}
    </div>
  );
}
