import { useEffect, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api } from "../../api/client";
import type { DeletionPreview, DeletionResult } from "../../api/types";
import { Button } from "../../components/Button";
import { Dialog } from "../../components/Dialog";
import { Field, TextInput } from "../../components/Field";
import { useToast } from "../../components/Toast";

/**
 * Permanent deletion of the assets selected in a register (only for assets created by mistake).
 * Shows which ones can go and why the others cannot, and asks to type the confirmation word, as on GitHub.
 */
export function DeleteAssetsDialog({ open, onClose, assetIds, onDone }: { open: boolean; onClose: () => void; assetIds: string[]; onDone: () => void }) {
  const toast = useToast();
  const client = useQueryClient();
  const [typed, setTyped] = useState("");
  useEffect(() => {
    if (open) setTyped("");
  }, [open]);

  const preview = useQuery({
    queryKey: ["delete-preview", assetIds],
    queryFn: ({ signal }) => api<DeletionPreview>("/api/assets/delete/preview", { method: "POST", body: { assetIds }, signal }),
    enabled: open && assetIds.length > 0,
    gcTime: 0,
  });
  const word = preview.data?.confirmationWord ?? "DELETE";
  const blocked = preview.data?.items.filter((i) => i.blocked) ?? [];
  const deletable = preview.data?.deletable ?? 0;

  const remove = useMutation({
    mutationFn: () => api<DeletionResult>("/api/assets/delete", { method: "POST", body: { assetIds, confirmation: typed } }),
    onSuccess: (r) => {
      client.invalidateQueries({ queryKey: ["register"] });
      client.invalidateQueries({ queryKey: ["dashboard"] });
      client.invalidateQueries({ queryKey: ["groups"] });
      toast(`${r.deleted} asset${r.deleted === 1 ? "" : "s"} deleted${r.notDeleted.length > 0 ? `; ${r.notDeleted.length} kept` : ""}.`);
      onDone();
      onClose();
    },
    onError: (e) => toast(e.message, "error"),
  });

  return (
    <Dialog open={open} title={`Delete ${assetIds.length} asset${assetIds.length === 1 ? "" : "s"}`} onClose={onClose}>
      {preview.isPending && <p className="text-muted">Checking the selected assets…</p>}
      {preview.error && <p className="text-hold">{preview.error.message}</p>}
      {preview.data && (
        <>
          <p>
            <strong>{deletable}</strong> asset{deletable === 1 ? "" : "s"} will be <strong>deleted permanently</strong>, with their gates, documentation rows,
            custom fields and group memberships. This cannot be undone. Use it only for assets created by mistake; deactivate anything that really existed.
          </p>
          {blocked.length > 0 && (
            <div className="mt-3 rounded-md border border-hold/40 p-3">
              <p className="font-semibold text-hold">{blocked.length} cannot be deleted and will stay as they are:</p>
              <ul className="mt-1 max-h-40 space-y-1 overflow-y-auto text-sm">
                {blocked.map((i) => (
                  <li key={i.id}><span className="font-semibold">{i.tag ?? i.code}</span> <span className="text-muted">({i.code})</span>: {i.blocked}</li>
                ))}
              </ul>
            </div>
          )}
          {deletable > 0 && (
            <div className="mt-4">
              <Field label={`Type ${word} to confirm`}>
                <TextInput value={typed} onChange={(e) => setTyped(e.target.value)} autoComplete="off" spellCheck={false} />
              </Field>
            </div>
          )}
        </>
      )}
      <div className="mt-5 flex justify-end gap-2">
        <Button variant="secondary" onClick={onClose}>Cancel</Button>
        <Button variant="danger" busy={remove.isPending} disabled={deletable === 0 || typed.trim() !== word} onClick={() => remove.mutate()}>
          Delete {deletable} permanently
        </Button>
      </div>
    </Dialog>
  );
}
