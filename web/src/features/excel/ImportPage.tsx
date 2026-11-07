import { useMemo, useState } from "react";
import { Link, useParams } from "react-router";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api, download } from "../../api/client";
import type { ImportChange, ImportRunDetail } from "../../api/types";
import { Button } from "../../components/Button";
import { PageHeader } from "../../components/PageHeader";
import { formatDateTime } from "../../components/dates";
import { useToast } from "../../components/Toast";
import { OutcomeChip, StatusChip, isIssue } from "./ImportBits";

type Filter = "all" | "changes" | "issues" | "nochange";

/** Preview of an uploaded file (nothing saved yet) or the record of an applied one. */
export function ImportPage() {
  const id = Number(useParams().id);
  const queryClient = useQueryClient();
  const toast = useToast();
  const [filter, setFilter] = useState<Filter>("all");
  const [decisions, setDecisions] = useState<Record<number, Decision>>({});
  const detail = useQuery({
    queryKey: ["import", id],
    queryFn: ({ signal }) => api<ImportRunDetail>(`/api/imports/${id}`, { signal }),
  });

  const act = useMutation({
    mutationFn: (action: "apply" | "cancel") => api<ImportRunDetail>(`/api/imports/${id}/${action}`, {
      method: "POST",
      body: action === "apply" && Object.keys(decisions).length > 0 ? { decisions: Object.fromEntries(Object.entries(decisions).filter(([, d]) => d !== "review")) } : undefined,
    }),
    onSuccess: (d, action) => {
      queryClient.setQueryData(["import", id], d);
      queryClient.invalidateQueries({ queryKey: ["excel"] });
      queryClient.invalidateQueries({ queryKey: ["dashboard"] });
      queryClient.invalidateQueries({ queryKey: ["register"] });
      queryClient.invalidateQueries({ queryKey: ["issues"] });
      queryClient.invalidateQueries({ queryKey: ["monitoring"] });
      if (action === "cancel") toast("Preview cancelled", "info");
      else if (d.run.status === "Rejected") toast(d.run.message ?? "The file was rejected.", "error");
      else toast(`Applied: ${d.run.counts.applied + d.run.counts.derived + d.run.counts.created} changes saved`);
    },
    onError: (e) => toast(e.message, "error"),
  });

  const changes = detail.data?.changes ?? [];
  const groups = useMemo(() => {
    const visible = changes.filter((c) =>
      filter === "all" ? c.outcome !== "No change"
        : filter === "changes" ? ["Applied", "Derived", "Created"].includes(c.outcome)
          : filter === "issues" ? isIssue(c.outcome)
            : c.outcome === "No change");
    const byAsset = new Map<string, ImportChange[]>();
    for (const c of visible) {
      const key = c.assetId ?? `row-${c.rowNumber}-${c.assetTag}`;
      byAsset.set(key, [...(byAsset.get(key) ?? []), c]);
    }
    return [...byAsset.values()];
  }, [changes, filter]);

  if (detail.isPending) return <p className="text-muted">Loading…</p>;
  if (detail.isError) return <p className="text-hold">{detail.error.message}</p>;

  const { run } = detail.data;
  const c = run.counts;
  const preview = run.status === "Previewed";
  // E7: motors with a complete (locked) test whose values the file changes. Each one needs a Yes / No before applying.
  const replaceQuestions = preview ? changes.filter((x) => x.field === ReplaceTestField && x.outcome === "Conflict" && x.rowNumber != null) : [];
  const unanswered = replaceQuestions.filter((x) => !["replace", "keep"].includes(decisions[x.rowNumber!] ?? "")).length;
  // Rows answered No load none of their test values: they leave the count.
  const keptRows = new Set(replaceQuestions.filter((x) => decisions[x.rowNumber!] === "keep").map((x) => x.rowNumber));
  const notLoaded = changes.filter((x) => x.outcome === "Applied" && keptRows.has(x.rowNumber) && !["NameplateData", "Required"].includes(x.field)).length;
  const changeCount = c.applied + c.derived + c.created - notLoaded;

  return (
    <div>
      <PageHeader
        title={run.kind === "Onboarding" ? "Client list" : run.kind === "ElectricalTest" ? "E7 electrical test sheet" : run.kind === "Readings" ? "Monitoring readings" : "Excel upload"}
        description={`${run.fileName} · ${run.unitName ?? ""} · uploaded ${formatDateTime(run.uploadedAtUtc)} by ${run.uploadedBy}`}
        actions={
          <>
            {run.unitId && <Link to={`/u/${run.unitId}/${run.kind === "Readings" ? "monitoring" : "excel"}`} className="inline-flex min-h-10 items-center rounded-md border border-line bg-panel px-4 font-semibold text-ink hover:border-violet hover:text-violet">{run.kind === "Readings" ? "Back to Monitoring" : "Back to Excel"}</Link>}
            {run.status !== "Rejected" && <Button variant="secondary" onClick={() => download(`/api/imports/${id}/file`).catch((e) => toast(e.message, "error"))}>Download uploaded file</Button>}
          </>
        }
      />

      <div className="mt-5 flex flex-wrap items-center gap-3">
        <StatusChip status={run.status} />
        {run.exportedAtUtc && <span className="text-sm text-muted">File downloaded {formatDateTime(run.exportedAtUtc)} by {run.exportedBy}</span>}
        {run.appliedAtUtc && <span className="text-sm text-muted">Applied {formatDateTime(run.appliedAtUtc)}</span>}
        {run.lineFormatCheckOff && <span className="rounded border border-ongoing/40 bg-ongoing/10 px-1.5 py-0.5 text-sm font-semibold text-ongoing">Line number format check off: non-standard numbers are created and marked</span>}
      </div>

      {run.status === "Rejected" && (
        <div className="mt-5 max-w-3xl rounded-lg border border-hold/30 bg-hold/5 p-5">
          <h2 className="text-xl font-semibold text-hold">The file was not imported</h2>
          <p className="mt-2">{run.message}</p>
        </div>
      )}

      {run.status !== "Rejected" && (
        <>
          <section className="mt-5 grid grid-cols-2 gap-3 sm:grid-cols-4" aria-label="Summary">
            <Count label={preview ? "Changes to save" : "Changes saved"} value={changeCount} detail={`${c.applied} cells · ${c.derived} derived${c.created ? ` · ${c.created} new` : ""}`} />
            <Count label="Issues" value={c.invalid + c.conflict + c.rejected} detail={`${c.conflict} conflicts · ${c.invalid} invalid · ${c.rejected} rejected`} tone={c.invalid + c.conflict + c.rejected > 0 ? "hold" : undefined} />
            <Count label="Warnings" value={c.warning} detail="Applied, but check them" tone={c.warning > 0 ? "ongoing" : undefined} />
            <Count label="Rows in file" value={run.rowsInFile} detail={`${c.noChange} cells already up to date`} />
          </section>

          {run.message && <p className="mt-4 max-w-3xl text-sm text-muted">{run.message}</p>}

          {preview && (
            <div className="sticky top-14 z-10 mt-5 flex flex-wrap items-center gap-3 rounded-lg border border-violet/30 bg-violet-soft p-4 lg:top-2">
              <p className="mr-auto max-w-2xl">
                <strong>Nothing is saved yet.</strong> Applying compares the file again with the data as it is at that moment, saves the valid changes
                and records the issues for review. The file cannot be uploaded again afterwards.
              </p>
              <Button variant="secondary" busy={act.isPending && act.variables === "cancel"} onClick={() => act.mutate("cancel")}>Cancel</Button>
              <Button busy={act.isPending && act.variables === "apply"} disabled={(changeCount === 0 && c.issues === 0) || unanswered > 0} onClick={() => act.mutate("apply")}>
                Apply {changeCount} changes
              </Button>
              {unanswered > 0 && <p className="w-full text-right text-sm font-semibold text-ongoing">Answer the {unanswered} E7 question{unanswered === 1 ? "" : "s"} first.</p>}
            </div>
          )}

          {replaceQuestions.length > 0 && (
            <div role="alert" className="mt-4 max-w-3xl rounded-lg border border-ongoing/40 bg-ongoing/10 p-4">
              <p className="font-semibold text-ink">
                {replaceQuestions.length === 1 ? "1 motor already has" : `${replaceQuestions.length} motors already have`} a complete E7 test and the file changes its values.
              </p>
              <p className="mt-1 text-sm text-ink">
                For each one choose <strong>Yes</strong> to replace the test values with those of the file, or <strong>No</strong> to keep the current test (none of the
                test values of that row are loaded). Values the test does not have yet, like the optional PI, load without asking.
              </p>
              <ul className="mt-3 space-y-2">
                {replaceQuestions.map((q) => (
                  <li key={q.id} className="flex flex-wrap items-center gap-x-3 gap-y-1">
                    <span className="mr-auto font-display font-semibold text-ink">{q.assetTag ?? q.assetCode} <span className="text-sm font-normal text-muted">· {q.excelValue}</span></span>
                    <ReplaceChoice row={q.rowNumber!} value={decisions[q.rowNumber!]} onDecide={(row, d) => setDecisions({ ...decisions, [row]: d })} />
                  </li>
                ))}
              </ul>
            </div>
          )}

          <div className="mt-6 flex flex-wrap gap-2" role="tablist" aria-label="Show">
            {([["all", "Everything"], ["changes", "Changes"], ["issues", "Issues and warnings"], ["nochange", "Already up to date"]] as [Filter, string][]).map(([k, label]) => (
              <button key={k} type="button" role="tab" aria-selected={filter === k} onClick={() => setFilter(k)}
                className={`min-h-9 rounded-full border px-3 text-sm font-semibold ${filter === k ? "border-violet bg-violet text-white" : "border-line bg-panel text-ink hover:border-violet"}`}>
                {label}
              </button>
            ))}
          </div>

          {groups.length === 0 ? (
            <p className="mt-4 text-muted">{changes.length === 0 ? "The file has no changes compared with what was downloaded." : "Nothing in this view."}</p>
          ) : (
            <ul className="mt-4 space-y-3">
              {groups.map((rows) => (
                <AssetChanges key={rows[0].id} rows={rows} preview={preview}
                  decision={(row) => decisions[row] ?? "review"} onDecide={(row, d) => setDecisions({ ...decisions, [row]: d })} />
              ))}
            </ul>
          )}
        </>
      )}
    </div>
  );
}

function Count({ label, value, detail, tone }: { label: string; value: number; detail: string; tone?: "hold" | "ongoing" }) {
  const border = tone === "hold" ? "border-hold/40" : tone === "ongoing" ? "border-ongoing/40" : "border-line";
  return (
    <div className={`rounded-lg border bg-panel p-4 ${border}`}>
      <p className="text-sm font-semibold text-muted">{label}</p>
      <p className="font-display text-[2rem] leading-none font-bold text-ink tabular-nums">{value}</p>
      <p className="mt-1 text-sm text-muted">{detail}</p>
    </div>
  );
}

type Decision = "review" | "update" | "rename" | "create" | "replace" | "keep";

/** Field of the E7 question "replace the complete test with the file values?" (server: ElectricalTestWorkbook.ReplaceTestField). */
const ReplaceTestField = "(replace test)";

/** Yes / No of the E7 question; nothing chosen until the user answers. */
function ReplaceChoice({ row, value, onDecide }: { row: number; value: Decision | undefined; onDecide: (row: number, d: Decision) => void }) {
  const option = (d: "replace" | "keep", label: string, on: string) => (
    <button type="button" aria-pressed={value === d} onClick={() => onDecide(row, d)}
      className={`min-h-9 rounded-md border px-3 text-sm font-semibold ${value === d ? on : "border-line bg-panel text-ink hover:border-violet"}`}>
      {label}
    </button>
  );
  return (
    <span className="flex gap-2" role="group" aria-label="Replace the E7 test?">
      {option("replace", "Yes, replace", "border-hold bg-hold text-white")}
      {option("keep", "No, keep current", "border-violet bg-violet text-white")}
    </span>
  );
}

function AssetChanges({ rows, preview, decision, onDecide }: { rows: ImportChange[]; preview: boolean; decision: (row: number) => Decision; onDecide: (row: number, d: Decision) => void }) {
  const first = rows[0];
  const title = first.assetTag ?? first.assetCode ?? `Row ${first.rowNumber}`;
  return (
    <li className="rounded-lg border border-line bg-panel">
      <div className="flex flex-wrap items-baseline gap-x-3 border-b border-line px-4 py-2.5">
        {first.assetId && first.assetCode ? (
          <Link to={`/assets/${first.assetId}`} className="font-display text-lg font-semibold text-ink hover:text-violet">{title}</Link>
        ) : (
          <span className="font-display text-lg font-semibold text-ink">{title}</span>
        )}
        <span className="text-sm text-muted">{[first.assetCode, first.rowNumber && `row ${first.rowNumber}`].filter(Boolean).join(" · ")}</span>
      </div>
      <ul>
        {rows.map((c) => (
          <li key={c.id} className="grid gap-x-4 gap-y-1 border-b border-line px-4 py-2.5 last:border-0 md:grid-cols-[12rem_1fr_auto]">
            <span className="font-semibold text-ink">{c.fieldLabel}</span>
            <span className="min-w-0">
              <ValueChange c={c} />
              {c.reason && <span className="block text-sm text-muted">{c.reason}</span>}
              {c.resolution === "Resolved" && <span className="block text-sm text-done">Resolved by {c.resolvedBy}{c.resolutionNote ? `: ${c.resolutionNote}` : ""}</span>}
              {preview && c.field === "(possible match)" && c.outcome === "Conflict" && c.rowNumber != null && (
                <span className="mt-2 flex flex-wrap items-center gap-2">
                  <label htmlFor={`d-${c.id}`} className="text-sm font-semibold text-ink">When applying:</label>
                  <select id={`d-${c.id}`} value={decision(c.rowNumber)} onChange={(e) => onDecide(c.rowNumber!, e.target.value as Decision)}
                    className="h-9 rounded-md border border-line bg-panel px-2 text-[15px] focus:border-violet focus:outline-none">
                    <option value="review">Leave it for review (nothing is loaded)</option>
                    <option value="update">Same line: load its values, keep {c.currentValue}</option>
                    <option value="rename">Same line: load its values and use {c.excelValue}</option>
                    <option value="create">Different line: create {c.excelValue}</option>
                  </select>
                </span>
              )}
              {preview && c.field === ReplaceTestField && c.outcome === "Conflict" && c.rowNumber != null && (
                <span className="mt-2 flex flex-wrap items-center gap-2">
                  <span className="text-sm font-semibold text-ink">Replace it with the file values?</span>
                  <ReplaceChoice row={c.rowNumber} value={decision(c.rowNumber)} onDecide={onDecide} />
                </span>
              )}
            </span>
            <span><OutcomeChip outcome={c.outcome} /></span>
          </li>
        ))}
      </ul>
    </li>
  );
}

function ValueChange({ c }: { c: ImportChange }) {
  const empty = <span className="text-muted italic">empty</span>;
  if (c.field === ReplaceTestField) return <span className="block break-words">{c.currentValue} · file: <strong>{c.excelValue}</strong>{c.newValue && <> → <strong>{c.newValue}</strong></>}</span>;
  if (c.field === "(possible match)") return <span className="block break-words">File: <strong>{c.excelValue}</strong> · in K Data: <strong>{c.currentValue}</strong></span>;
  if (c.outcome === "Conflict") {
    return (
      <span className="block break-words">
        File: <strong>{c.excelValue ?? empty}</strong> · app now: <strong>{c.currentValue ?? empty}</strong> · at download: {c.baseValue ?? empty}
      </span>
    );
  }
  if (c.outcome === "Warning" && c.field !== "Tag") return null;
  if (c.field === "(row)" || c.field === "(asset)") return c.newValue ? <span className="block">Equipment ID <strong>{c.newValue}</strong></span> : null;
  const from = c.outcome === "Invalid" || c.outcome === "Rejected" ? c.currentValue : (c.currentValue ?? c.baseValue);
  const to = c.outcome === "Invalid" || c.outcome === "Rejected" ? c.excelValue : (c.newValue ?? c.excelValue);
  if (from === null && to === null) return null;
  return (
    <span className="block break-words whitespace-pre-line">
      {from ?? empty} <span aria-label="becomes" className="text-muted">→</span> <strong>{to ?? empty}</strong>
    </span>
  );
}
