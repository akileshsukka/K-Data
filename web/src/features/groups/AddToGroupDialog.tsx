import { useState } from "react";
import { Link } from "react-router";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api } from "../../api/client";
import type { Group, MembersResult } from "../../api/types";
import { Button } from "../../components/Button";
import { Dialog } from "../../components/Dialog";
import { Field, SelectInput } from "../../components/Field";
import { useToast } from "../../components/Toast";

/** Adds the assets selected in a register to an existing workpack or group of the unit. */
export function AddToGroupDialog({ open, onClose, unitId, assetIds, onDone }: { open: boolean; onClose: () => void; unitId: number; assetIds: string[]; onDone: () => void }) {
  const toast = useToast();
  const client = useQueryClient();
  const groups = useQuery({ queryKey: ["groups", unitId, false], queryFn: ({ signal }) => api<Group[]>(`/api/groups?unitId=${unitId}&includeInactive=false`, { signal }), enabled: open });
  const [groupId, setGroupId] = useState(0);
  const chosen = groupId || groups.data?.[0]?.id || 0;
  const add = useMutation({
    mutationFn: () => api<MembersResult>(`/api/groups/${chosen}/members`, { method: "POST", body: { assetIds } }),
    onSuccess: (r) => {
      client.invalidateQueries({ queryKey: ["groups"] });
      client.invalidateQueries({ queryKey: ["register"] });
      client.setQueryData(["group", chosen], r.detail);
      toast(`${r.added} added to ${r.detail.group.code}${r.alreadyIn > 0 ? ` (${r.alreadyIn} already in it)` : ""}`);
      onDone();
      onClose();
    },
    onError: (e) => toast(e.message, "error"),
  });
  return (
    <Dialog open={open} title={`Add ${assetIds.length} to a group`} onClose={onClose}>
      {groups.data && groups.data.length === 0 ? (
        <p>There are no groups in this unit yet. <Link className="font-semibold text-violet" to={`/u/${unitId}/groups`}>Create one first</Link>.</p>
      ) : (
        <Field label="Workpack or group">
          <SelectInput value={chosen} onChange={(e) => setGroupId(Number(e.target.value))}>
            {(groups.data ?? []).map((g) => <option key={g.id} value={g.id}>{g.code} — {g.name} ({g.type})</option>)}
          </SelectInput>
        </Field>
      )}
      <div className="mt-5 flex justify-end gap-2">
        <Button variant="secondary" onClick={onClose}>Cancel</Button>
        <Button busy={add.isPending} disabled={!chosen} onClick={() => add.mutate()}>Add</Button>
      </div>
    </Dialog>
  );
}
