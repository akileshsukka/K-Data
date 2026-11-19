import { useMemo, useState } from "react";
import { Link, useNavigate, useParams } from "react-router";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api } from "../../api/client";
import type { SplitGate, SplitPreview, SplitResult } from "../../api/types";
import { Button } from "../../components/Button";
import { SelectInput, TextInput } from "../../components/Field";
import { GateStatus, gatePrefix } from "../../components/GateStatus";
import { PageHeader } from "../../components/PageHeader";
import { useToast } from "../../components/Toast";
import { formatDateTime } from "../../components/dates";

const MIN_PARTS = 2;
const MAX_PARTS = 6;
/** Reading target: a part index, or "all" (copied to every part). */
type Target = number | "all";

/**
 * Split an asset the client lists under one grouped tag (P-101A/B) into separate assets worked on separately.
 * The existing asset becomes the first part (keeps its Equipment ID, Simpro flange and history); the others are new.
 * The engineer sets the gates of every part and says which part each reading belongs to; nothing is saved until confirmed.
 */
export function SplitAssetPage() {
  const id = useParams().id!;
  const preview = useQuery({
    queryKey: ["asset", id, "split"],
    queryFn: ({ signal }) => api<SplitPreview>(`/api/assets/${id}/split`, { signal }),
    gcTime: 0,
    refetchOnWindowFocus: false,
  });
  if (preview.isPending) return <p className="text-muted">Loading…</p>;
  if (preview.error) return <p className="text-hold">{preview.error.message}</p>;
  return <SplitForm key={preview.data.stamp} preview={preview.data} reload={() => preview.refetch()} />;
}

function SplitForm({ preview, reload }: { preview: SplitPreview; reload: () => void }) {
  const toast = useToast();
  const navigate = useNavigate();
  const client = useQueryClient();
  const g = gatePrefix(preview.type);
  const source = preview.clientTag ?? preview.tag ?? preview.code;

  const [tags, setTags] = useState<string[]>(() =>
    preview.suggestedTags.length >= MIN_PARTS ? preview.suggestedTags : [`${source}A`, `${source}B`],
  );
  const [gates, setGates] = useState<SplitGate[][]>(() => tags.map(() => preview.gates.map((x) => ({ ...x }))));
  const [targets, setTargets] = useState<Record<number, Target>>({});
  const [touched, setTouched] = useState<Set<number>>(new Set());

  // Readings follow the note ("B", "P-101B") until the engineer picks a part by hand.
  const suggested = useMemo(() => Object.fromEntries(preview.readings.map((r) => [r.id, suggestPart(r.note, tags)])), [preview.readings, tags]);
  const targetOf = (readingId: number): Target => (touched.has(readingId) ? targets[readingId] : suggested[readingId]) ?? "all";

  function setCount(n: number) {
    setTags((t) => Array.from({ length: n }, (_, i) => t[i] ?? `${source}${String.fromCharCode(65 + i)}`));
    setGates((all) => Array.from({ length: n }, (_, i) => all[i] ?? preview.gates.map((x) => ({ ...x }))));
    setTargets((t) => Object.fromEntries(Object.entries(t).map(([k, v]) => [k, typeof v === "number" && v >= n ? "all" : v])));
  }

  function setGate(part: number, gate: number, change: Partial<SplitGate>) {
    setGates((all) => all.map((row, i) => (i !== part ? row : row.map((x) => (x.gate === gate ? { ...x, ...change } : x)))));
  }

  const split = useMutation({
    mutationFn: () =>
      api<SplitResult>(`/api/assets/${preview.id}/split`, {
        method: "POST",
        body: {
          stamp: preview.stamp,
          parts: tags.map((tag, i) => ({ tag: tag.trim(), gates: gates[i] })),
          readings: preview.readings.map((r) => ({ id: r.id, part: targetOf(r.id) === "all" ? null : targetOf(r.id) })),
        },
      }),
    onSuccess: (r) => {
      client.invalidateQueries({ queryKey: ["asset"] });
      client.invalidateQueries({ queryKey: ["register"] });
      client.invalidateQueries({ queryKey: ["unit-summary"] });
      client.invalidateQueries({ queryKey: ["dashboard"] });
      client.invalidateQueries({ queryKey: ["groups"] });
      toast(`${r.clientTag} split into ${r.parts.map((p) => `${p.tag} (${p.code})`).join(", ")}.`);
      navigate(`/assets/${r.parts[0].id}`);
      window.scrollTo(0, 0);
    },
    onError: (e) => toast(e.message, "error"),
  });

  const tagProblem = tagError(tags, source);
  const clientChanges = preview.gates.filter((now) => (combined(gates.map((row) => row[now.gate - 1].status)) ?? "") !== (now.status ?? ""));

  function confirm() {
    const moved = preview.readings.filter((r) => targetOf(r.id) !== 0 && targetOf(r.id) !== "all").length;
    const copied = preview.readings.filter((r) => targetOf(r.id) === "all").length;
    const text =
      `Split ${source} into ${tags.join(", ")}?\n\n` +
      `${tags[0]} keeps ${preview.code}${preview.inSimpro ? " and its Simpro flange" : ""}; ${tags.length - 1 === 1 ? "the other one is created with a new Equipment ID" : "the others are created with new Equipment IDs"}.\n` +
      `${moved} reading(s) move, ${copied} are copied to every part.\n\nThis is not undone automatically.`;
    if (window.confirm(text)) split.mutate();
  }

  return (
    <div>
      <Link to={`/assets/${preview.id}`} className="text-sm font-semibold text-violet">{preview.tag ?? preview.code}</Link>
      <PageHeader
        title={`Split ${preview.tag ?? preview.code}`}
        description={`${preview.code} — ${preview.unit}. For an item the client lists under one tag but that is worked on as separate equipment.`}
      />

      {preview.blocked ? (
        <p className="mt-5 rounded-md border border-hold/40 p-3 text-hold">{preview.blocked}</p>
      ) : (
        <>
          <section className="mt-5 rounded-lg border border-line bg-panel p-4">
            <h2 className="text-lg font-semibold">What happens</h2>
            <ul className="mt-2 list-disc space-y-1 pl-5 text-[15px]">
              <li><strong>{tags[0] || "The first part"}</strong> is this asset: it keeps {preview.code}, {preview.inSimpro ? "its Simpro flange, " : ""}its history and the readings you leave on it.</li>
              <li>The other parts are <strong>new assets</strong> with their own Equipment ID. They get a copy of the preservation data, {preview.customFields} custom field value(s), {preview.groups} workpack/group membership(s), {preview.documentedGates} gate documentation row(s) and {preview.documents} linked document(s) (the files are not duplicated).</li>
              {preview.electricalTestRequired && <li>The {g}7 electrical test values stay on {tags[0] || "the first part"}; the new parts only inherit that they need one.</li>}
              <li>All parts keep the client tag <strong>{source}</strong>, so the unit Excel shows them as one row again, as the client lists it. A change made to that row in the Excel waits in Import issues for you to say which parts it is for.</li>
              <li>Simpro: {preview.inSimpro ? `the flange of ${preview.code} gets the new tag, and ` : ""}the new parts are created by the next sync. Check the Simpro preview before syncing.</li>
              <li>An Excel of {preview.unit} downloaded before the split only changes {tags[0] || "the first part"}: download a new one.</li>
            </ul>
          </section>

          <section className="mt-5 rounded-lg border border-line bg-panel p-4">
            <div className="flex flex-wrap items-end justify-between gap-3">
              <h2 className="text-lg font-semibold">Parts</h2>
              <label className="flex items-center gap-2 text-sm whitespace-nowrap">
                Number of parts
                <SelectInput value={tags.length} onChange={(e) => setCount(Number(e.target.value))} className="mt-0 w-20">
                  {Array.from({ length: MAX_PARTS - MIN_PARTS + 1 }, (_, i) => i + MIN_PARTS).map((n) => <option key={n} value={n}>{n}</option>)}
                </SelectInput>
              </label>
            </div>
            <div className="mt-3 grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
              {tags.map((tag, i) => (
                <label key={i} className="block text-sm">
                  <span className="text-muted">{i === 0 ? `Part 1 — keeps ${preview.code}` : `Part ${i + 1} — new asset`}</span>
                  <TextInput value={tag} maxLength={55} onChange={(e) => setTags((t) => t.map((x, j) => (j === i ? e.target.value : x)))} aria-label={`Tag of part ${i + 1}`} />
                </label>
              ))}
            </div>
            {tagProblem && <p className="mt-2 text-sm text-hold">{tagProblem}</p>}
          </section>

          <section className="mt-5 rounded-lg border border-line bg-panel">
            <header className="border-b border-line px-4 py-3">
              <h2 className="text-lg font-semibold">Gates</h2>
              <p className="text-sm text-muted">Every part starts with the current values. Correct the ones that do not apply, e.g. if only one part finished {g}2.</p>
            </header>
            <div className="overflow-x-auto">
              <table className="w-full min-w-[40rem] text-left text-[15px]">
                <thead className="text-sm text-muted">
                  <tr className="border-b border-line">
                    <th className="px-4 py-2 font-semibold">Gate</th>
                    <th className="px-3 py-2 font-semibold">Now</th>
                    {tags.map((t, i) => <th key={i} className="px-3 py-2 font-semibold">{t || `Part ${i + 1}`}</th>)}
                    <th className="px-3 py-2 font-semibold">Client Excel</th>
                  </tr>
                </thead>
                <tbody>
                  {preview.gates.map((now) => {
                    const shown = combined(gates.map((row) => row[now.gate - 1].status));
                    const differs = (shown ?? "") !== (now.status ?? "");
                    return (
                      <tr key={now.gate} className="border-b border-line/70 align-top last:border-b-0">
                        <td className="px-4 py-2 font-semibold">{g}{now.gate}</td>
                        <td className="px-3 py-2"><GateStatus value={now.status} compact /></td>
                        {gates.map((row, i) => {
                          const cell = row[now.gate - 1];
                          const changed = (cell.status ?? "") !== (now.status ?? "") || (cell.comments ?? "") !== (now.comments ?? "");
                          return (
                            <td key={i} className={`px-3 py-2 ${changed ? "bg-violet-soft" : ""}`}>
                              <SelectInput value={cell.status ?? ""} onChange={(e) => setGate(i, now.gate, { status: e.target.value || null })} className="mt-0 w-full min-w-36" aria-label={`${g}${now.gate} status of ${tags[i]}`}>
                                <option value="">—</option>
                                {withCurrent(preview.statusOptions, cell.status).map((o) => <option key={o} value={o}>{o}</option>)}
                              </SelectInput>
                              <TextInput value={cell.comments ?? ""} placeholder="Comments" onChange={(e) => setGate(i, now.gate, { comments: e.target.value || null })} className="mt-1 w-full text-sm" aria-label={`${g}${now.gate} comments of ${tags[i]}`} />
                            </td>
                          );
                        })}
                        <td className="px-3 py-2">
                          <GateStatus value={shown} compact />
                          {differs && <p className="mt-1 text-xs text-ongoing">Changes what the client sees</p>}
                        </td>
                      </tr>
                    );
                  })}
                </tbody>
              </table>
            </div>
            <p className="border-t border-line px-4 py-2 text-sm text-muted">
              Client Excel: the most advanced status among the parts (Completed &gt; Ongoing &gt; Hold); Not Applicable only when all parts are N/A.
              Current Gate of a part follows its gates when you change them.
            </p>
            {clientChanges.length > 0 && (
              <p className="border-t border-line px-4 py-2 text-sm text-ongoing">
                {clientChanges.map((x) => `${g}${x.gate}`).join(", ")} will look different to the client than today.
              </p>
            )}
          </section>

          <section className="mt-5 rounded-lg border border-line bg-panel">
            <header className="border-b border-line px-4 py-3">
              <h2 className="text-lg font-semibold">Readings ({preview.readings.length})</h2>
              <p className="text-sm text-muted">
                Say which part each reading belongs to. A note with a part's letter or tag picks it already. "All parts" copies the reading to every part, marked as copied.
              </p>
            </header>
            {preview.readings.length === 0 ? (
              <p className="px-4 py-3 text-muted">No readings.</p>
            ) : (
              <div className="max-h-[28rem] overflow-auto">
                <table className="w-full min-w-[40rem] text-left text-[15px]">
                  <thead className="sticky top-0 bg-panel text-sm text-muted">
                    <tr className="border-b border-line">
                      <th className="px-4 py-2 font-semibold">Measured</th>
                      <th className="px-3 py-2 font-semibold">Measurement</th>
                      <th className="px-3 py-2 text-right font-semibold">Value</th>
                      <th className="px-3 py-2 font-semibold">Note</th>
                      <th className="px-3 py-2 font-semibold">Goes to</th>
                    </tr>
                  </thead>
                  <tbody>
                    {preview.readings.map((r) => {
                      const t = targetOf(r.id);
                      return (
                        <tr key={r.id} className="border-b border-line/70 last:border-b-0">
                          <td className="px-4 py-2 whitespace-nowrap">{formatDateTime(r.measuredAtUtc)}</td>
                          <td className="px-3 py-2">{r.measurement}</td>
                          <td className="px-3 py-2 text-right tabular-nums">{r.value} {r.unitOfMeasure}</td>
                          <td className="px-3 py-2 text-muted">{r.note ?? "—"}</td>
                          <td className="px-3 py-2">
                            <SelectInput
                              value={String(t)}
                              onChange={(e) => {
                                const v = e.target.value === "all" ? "all" : Number(e.target.value);
                                setTargets((x) => ({ ...x, [r.id]: v }));
                                setTouched((x) => new Set(x).add(r.id));
                              }}
                              className="mt-0 min-w-36"
                              aria-label={`Part of the reading of ${formatDateTime(r.measuredAtUtc)}`}
                            >
                              <option value="all">All parts (copy)</option>
                              {tags.map((tag, i) => <option key={i} value={i}>{tag || `Part ${i + 1}`}</option>)}
                            </SelectInput>
                          </td>
                        </tr>
                      );
                    })}
                  </tbody>
                </table>
              </div>
            )}
          </section>

          <div className="mt-5 flex flex-wrap justify-end gap-2">
            <Button variant="secondary" onClick={reload}>Reload</Button>
            <Link to={`/assets/${preview.id}`} className="inline-flex min-h-10 items-center rounded-md border border-line px-4 font-semibold hover:border-violet hover:text-violet">Cancel</Link>
            <Button busy={split.isPending} disabled={!!tagProblem} onClick={confirm}>Split into {tags.length} assets</Button>
          </div>
        </>
      )}
    </div>
  );
}


/** Same checks as the server, so the button is off until the tags can be saved. */
function tagError(tags: string[], clientTag: string): string | null {
  const clean = tags.map((t) => t.trim());
  if (clean.some((t) => !t)) return "Enter the tag of every part.";
  const lower = clean.map((t) => t.toLowerCase());
  if (new Set(lower).size !== lower.length) return "Two parts have the same tag.";
  if (lower.includes(clientTag.trim().toLowerCase())) return `${clientTag} is the client's tag; give each part its own tag.`;
  return null;
}

/** The status the client Excel shows for one gate: the most advanced among the parts; N/A only when all are N/A. */
export function combined(statuses: (string | null)[]): string | null {
  const values = statuses.map((s) => s?.trim() || null);
  const applicable = values.filter((s) => s?.toLowerCase() !== "not applicable");
  if (applicable.length === 0) return values.length > 0 && values.every((s) => s) ? "Not Applicable" : null;
  const rank = (s: string | null) => (!s ? 0 : s.toLowerCase() === "completed" ? 4 : s.toLowerCase() === "ongoing" ? 3 : s.toLowerCase() === "hold" ? 1 : 2);
  return applicable.reduce((best, s) => (rank(s) > rank(best) ? s : best), null as string | null);
}

/** The part a reading note points to: its full tag, or the letter that tells the parts apart ("B", "B only"). */
export function suggestPart(note: string | null, tags: string[]): Target | undefined {
  if (!note) return undefined;
  const text = note.toUpperCase();
  const clean = tags.map((t) => t.trim().toUpperCase());
  // What tells the parts apart: the text between the common start and the common end ("A" in P-101A, "043" in "(S/N 043)"),
  // also widened to whole words ("101A"), so a note "B", "043" or "A1" finds its part.
  let pre = 0;
  while (clean.every((t) => t.length > pre && t[pre] === clean[0][pre])) pre++;
  let suf = 0;
  while (clean.every((t) => t.length - suf > pre && t[t.length - 1 - suf] === clean[0][clean[0].length - 1 - suf])) suf++;
  const alnum = /[A-Z0-9]/;
  const words = new Set(text.split(/[^A-Z0-9]+/).filter(Boolean));
  const hits = clean
    .map((t, i) => {
      const raw = t.slice(pre, t.length - suf);
      let a = pre;
      let b = t.length - suf;
      while (a > 0 && alnum.test(t[a - 1])) a--;
      while (b < t.length && alnum.test(t[b])) b++;
      const wide = t.slice(a, b);
      return { i, hit: (!!t && text.includes(t)) || (!!raw && words.has(raw)) || (!!wide && words.has(wide)) };
    })
    .filter((x) => x.hit);
  return hits.length === 1 ? hits[0].i : undefined;
}

/** A value no longer in the list (renamed option) is still shown, so it is not lost silently. */
function withCurrent(options: string[], current: string | null) {
  return current && !options.includes(current) ? [...options, current] : options;
}
