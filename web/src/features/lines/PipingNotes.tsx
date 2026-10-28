import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api, ApiError } from "../../api/client";
import { Perm } from "../../api/types";
import { useSession } from "../../app/session";
import { Button } from "../../components/Button";
import { TextInput } from "../../components/Field";
import { useToast } from "../../components/Toast";
import { formatDate, todayDubai } from "../../components/dates";

interface PipingNote {
  id: number;
  date: string | null;
  text: string;
  updatedBy: string;
  updatedAtUtc: string;
}

interface Draft {
  date: string;
  text: string;
  /** Text the user started from (null = new note). */
  expected: string | null;
  isNew: boolean;
}

/**
 * Notes written under the Summary of the piping line list: a general note (e.g. the estimated cleaning completion date)
 * and one note per date. They go to the downloaded line list and come back from it.
 */
export function PipingNotes({ unitId }: { unitId: number }) {
  const session = useSession();
  const toast = useToast();
  const client = useQueryClient();
  const [open, setOpen] = useState(false);
  const [draft, setDraft] = useState<Draft | null>(null);
  const notes = useQuery({ queryKey: ["piping-notes", unitId], queryFn: ({ signal }) => api<PipingNote[]>(`/api/units/${unitId}/piping-notes`, { signal }) });
  const canEdit = session.can(Perm.assetsEdit);
  const save = useMutation({
    mutationFn: (d: { date: string | null; text: string | null; expected: string | null }) =>
      api<PipingNote[]>(`/api/units/${unitId}/piping-notes`, { method: "PUT", body: d }),
    onSuccess: (list) => {
      client.setQueryData(["piping-notes", unitId], list);
      setDraft(null);
      toast("Note saved");
    },
    onError: (e) => toast(e instanceof ApiError ? e.message : "Could not save.", "error"),
  });

  const list = notes.data ?? [];
  const latest = list.filter((n) => n.date).at(-1);

  function submit() {
    if (!draft) return;
    const date = draft.date || null;
    const existing = list.find((n) => n.date === date);
    if (draft.isNew && existing) {
      toast(`There is already a note for ${date ? formatDate(date) : "the general note"}: edit it instead.`, "error");
      return;
    }
    save.mutate({ date, text: draft.text.trim() || null, expected: draft.isNew ? null : draft.expected });
  }

  return (
    <section className="mt-4 rounded-lg border border-line bg-panel">
      <button className="flex w-full items-center justify-between gap-3 px-4 py-2.5 text-left" onClick={() => setOpen(!open)} aria-expanded={open}>
        <span>
          <span className="font-display text-lg font-semibold text-ink">Summary notes</span>
          <span className="ml-2 text-sm text-muted">
            {list.length === 0 ? "none yet" : latest ? `last: ${formatDate(latest.date)}` : "general note only"} · written under the Summary of the line list
          </span>
        </span>
        <span className="text-sm font-semibold text-violet">{open ? "Hide" : "Show"}</span>
      </button>
      {open && (
        <div className="border-t border-line px-4 py-3">
          {list.length === 0 && !draft && <p className="text-muted">No notes. They are also loaded from the Excel (lines under the Summary, each date as "27 Oct:").</p>}
          <ul className="space-y-3">
            {list.map((n) => (
              <li key={n.id} className="flex flex-wrap items-start justify-between gap-2">
                <div className="min-w-0 flex-1">
                  <p className="text-sm font-semibold text-ink">{n.date ? formatDate(n.date) : "General"}</p>
                  <p className="whitespace-pre-line">{n.text}</p>
                  <p className="text-sm text-muted">by {n.updatedBy}</p>
                </div>
                {canEdit && !draft && (
                  <Button variant="quiet" onClick={() => setDraft({ date: n.date ?? "", text: n.text, expected: n.text, isNew: false })}>Edit</Button>
                )}
              </li>
            ))}
          </ul>
          {draft && (
            <div className="mt-3 space-y-2 rounded-md border border-line p-3">
              <label className="block">
                <span className="text-sm font-semibold text-ink">Date</span>
                <TextInput type="date" value={draft.date} disabled={!draft.isNew} onChange={(e) => setDraft({ ...draft, date: e.target.value })} className="mt-1 w-48" />
                <span className="mt-1 block text-sm text-muted">Leave empty for the general note (e.g. estimated date of cleaning completion).</span>
              </label>
              <label className="block">
                <span className="text-sm font-semibold text-ink">Note</span>
                <textarea
                  value={draft.text}
                  rows={4}
                  maxLength={4000}
                  onChange={(e) => setDraft({ ...draft, text: e.target.value })}
                  className="mt-1 block w-full rounded-md border border-line bg-panel px-3 py-2 text-[15px] focus:border-violet focus:outline-none"
                />
              </label>
              <div className="flex flex-wrap gap-2">
                <Button busy={save.isPending} disabled={!draft.text.trim() && draft.isNew} onClick={submit}>Save</Button>
                {!draft.isNew && (
                  <Button variant="danger" busy={save.isPending} onClick={() => save.mutate({ date: draft.date || null, text: null, expected: draft.expected })}>Remove</Button>
                )}
                <Button variant="secondary" onClick={() => setDraft(null)}>Cancel</Button>
              </div>
            </div>
          )}
          {canEdit && !draft && (
            <Button variant="secondary" className="mt-3" onClick={() => setDraft({ date: todayDubai(), text: "", expected: null, isNew: true })}>Add note</Button>
          )}
        </div>
      )}
    </section>
  );
}
