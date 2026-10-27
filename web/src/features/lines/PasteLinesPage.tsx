import { useState } from "react";
import { Link, useNavigate, useParams } from "react-router";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { api, upload } from "../../api/client";
import { Perm, type BulkLineRow, type BulkLinesResult, type ImportRunDetail } from "../../api/types";
import { useLineCodes, useUnit } from "../../app/useCatalog";
import { useSession } from "../../app/session";
import { FileDrop } from "../excel/FileDrop";
import { CodeMeaning } from "./CodeMeaning";
import { Button } from "../../components/Button";
import { PageHeader } from "../../components/PageHeader";
import { useToast } from "../../components/Toast";
import { formatSize } from "./lineNumber";

/** Paste the client's line list (from Excel): one line per row, optionally its length in the next column. */
export function PasteLinesPage() {
  const unitId = Number(useParams().unitId);
  const { unit } = useUnit(unitId);
  const toast = useToast();
  const client = useQueryClient();
  const codes = useLineCodes();
  const session = useSession();
  const navigate = useNavigate();
  const [sending, setSending] = useState(false);

  async function sendList(file: File) {
    setSending(true);
    try {
      const detail = await upload<ImportRunDetail>(`/api/units/${unitId}/excel/onboarding`, file);
      navigate(`/imports/${detail.run.id}`);
    } catch (e) {
      toast(e instanceof Error ? e.message : "The file could not be checked.", "error");
      setSending(false);
    }
  }
  const [text, setText] = useState("");
  const [result, setResult] = useState<BulkLinesResult & { applied: boolean }>();

  const run = useMutation({
    mutationFn: (apply: boolean) =>
      api<BulkLinesResult>(`/api/units/${unitId}/lines/bulk${apply ? "?apply=true" : ""}`, { method: "POST", body: { text } }).then((r) => ({ ...r, applied: apply })),
    onSuccess: (r) => {
      setResult(r);
      if (r.applied) {
        client.invalidateQueries({ queryKey: ["register"] });
        client.invalidateQueries({ queryKey: ["unit-summary"] });
        toast(`${r.toCreate} line${r.toCreate === 1 ? "" : "s"} created`);
      }
    },
    onError: (e) => toast(e.message, "error"),
  });

  const order: Record<BulkLineRow["outcome"], number> = { Invalid: 0, "Possible match": 0, New: 1, Created: 1, Exists: 2 };
  const rows = [...(result?.rows ?? [])].sort((a, b) => order[a.outcome] - order[b.outcome] || a.row - b.row);

  return (
    <div>
      <Link to={`/u/${unitId}/lines`} className="text-sm font-semibold text-violet">Pipeline Register</Link>
      <PageHeader
        title="Add lines in bulk"
        description={unit ? `${unit.name} — paste line numbers to check them and create the new ones, or upload the client line list with its data.` : undefined}
      />

      <div className="mt-5 grid gap-5 xl:grid-cols-[24rem_minmax(0,1fr)]">
        <section>
          <label htmlFor="lines" className="font-semibold text-ink">Lines</label>
          <p className="text-sm text-muted">Copy the Line No. column from Excel (and the length next to it, if you have it). Size, spec and service are read from the line number; rating, face and gasket come from the pipe spec list.</p>
          <textarea
            id="lines"
            value={text}
            onChange={(e) => { setText(e.target.value); setResult(undefined); }}
            rows={16}
            spellCheck={false}
            placeholder={`${unit?.number ?? 39}-0218-LS-16"-61055-P\t120\n${unit?.number ?? 39}-0219-PG-3/4"-1C1`}
            className="mt-2 w-full rounded-md border border-line bg-panel p-3 font-mono text-[14px] leading-6 text-ink focus:border-violet focus:outline-none"
          />
          <ol className="mt-2 list-decimal pl-5 text-sm text-muted">
            <li><strong className="text-ink">Check the list</strong>: shows what each row becomes; nothing is created.</li>
            <li><strong className="text-ink">Create</strong>: appears after the check when there are new lines.</li>
          </ol>
          <div className="mt-3 flex flex-wrap justify-end gap-2">
            <Button variant="secondary" busy={run.isPending && run.variables === false} disabled={!text.trim()} onClick={() => run.mutate(false)}>Check the list</Button>
            {result && !result.applied && result.toCreate > 0 && (
              <Button busy={run.isPending && run.variables === true} onClick={() => run.mutate(true)}>Create {result.toCreate} line{result.toCreate === 1 ? "" : "s"}</Button>
            )}
          </div>

          {session.can(Perm.excelImport) && (
            <div className="mt-8 border-t border-line pt-5">
              <h2 className="font-display text-xl font-semibold text-ink">Client line list with data</h2>
              <p className="mt-1 text-sm text-muted">
                An .xlsx whose list has a <strong>Line No.</strong> column. A <strong>Circuit</strong> (or Workpack / System) column puts each line in that group, creating it if needed. Any other column K Data knows (Length, P&ID No., custom fields, gates G1–G8, Preservation Remarks, Remarks (ADNOC),
                Current Activity, Action By…) is loaded too. New line numbers are created, existing ones get the values of the list, and empty cells never clear anything.
                You see the preview before anything is applied.
              </p>
              <div className="mt-3"><FileDrop busy={sending} label="Choose the line list" onFile={sendList} /></div>
            </div>
          )}
        </section>

        <section aria-live="polite">
          {!result ? (
            <div className="rounded-lg border border-dashed border-line p-6 text-muted">The check shows what each row becomes before anything is created.</div>
          ) : (
            <>
              <div className="grid grid-cols-3 gap-3">
                <Count label={result.applied ? "Created" : "To create"} value={result.toCreate} />
                <Count label="Already in the register" value={result.existing} />
                <Count label="Not created" value={result.invalid + result.rows.filter((r) => r.outcome === "Possible match").length} tone={result.invalid > 0 || result.rows.some((r) => r.outcome === "Possible match")} />
              </div>
              <div className="mt-4 overflow-x-auto rounded-lg border border-line bg-panel">
                <table className="w-full border-separate border-spacing-0 text-left text-[15px]">
                  <thead className="bg-panel font-display text-muted">
                    <tr>
                      <th className="border-b border-line px-3 py-2 font-semibold">Row</th>
                      <th className="border-b border-line px-3 py-2 font-semibold">Line number</th>
                      <th className="border-b border-line px-3 py-2 text-right font-semibold">Size</th>
                      <th className="border-b border-line px-3 py-2 font-semibold">Spec</th>
                      <th className="border-b border-line px-3 py-2 font-semibold">Rating</th>
                      <th className="border-b border-line px-3 py-2 font-semibold">Face</th>
                      <th className="border-b border-line px-3 py-2 font-semibold">Service</th>
                      <th className="border-b border-line px-3 py-2 text-right font-semibold">Length</th>
                      <th className="border-b border-line px-3 py-2 font-semibold">Result</th>
                    </tr>
                  </thead>
                  <tbody>
                    {rows.map((r) => (
                      <tr key={r.row}>
                        <td className="border-b border-line px-3 py-2 text-muted tabular-nums">{r.row}</td>
                        <td className="border-b border-line px-3 py-2 font-mono text-[14px] whitespace-nowrap">
                          {r.assetId ? <Link to={`/assets/${r.assetId}`} className="text-ink hover:text-violet">{r.lineNumber}</Link> : r.lineNumber}
                          {r.code && <span className="block font-sans text-sm text-muted">{r.code}</span>}
                        </td>
                        <td className="border-b border-line px-3 py-2 text-right tabular-nums">{formatSize(r.sizeIn)}</td>
                        <td className="border-b border-line px-3 py-2">{r.pipeSpec}</td>
                        <td className="border-b border-line px-3 py-2 whitespace-nowrap">{r.pressureClass}</td>
                        <td className="border-b border-line px-3 py-2 whitespace-nowrap">{r.flangeType}</td>
                        <td className="max-w-[12rem] border-b border-line px-3 py-2"><CodeMeaning code={r.service} meaning={codes.describe("Service", r.service)} /></td>
                        <td className="border-b border-line px-3 py-2 text-right tabular-nums">{r.lengthM ?? ""}</td>
                        <td className="border-b border-line px-3 py-2">
                          <Outcome outcome={r.outcome} />
                          {r.reason && r.outcome === "Invalid" && <span className="block text-sm text-hold">{r.reason}</span>}
                          {r.reason && r.outcome === "Possible match" && <span className="block text-sm text-ongoing">{r.reason}</span>}
                          {r.warning && <span className="block text-sm text-ongoing">{r.warning}</span>}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            </>
          )}
        </section>
      </div>
    </div>
  );
}

function Count({ label, value, tone }: { label: string; value: number; tone?: boolean }) {
  return (
    <div className={`rounded-lg border bg-panel p-4 ${tone ? "border-hold/40" : "border-line"}`}>
      <p className="text-sm font-semibold text-muted">{label}</p>
      <p className="font-display text-[2rem] leading-none font-bold text-ink tabular-nums">{value}</p>
    </div>
  );
}

function Outcome({ outcome }: { outcome: BulkLineRow["outcome"] }) {
  const map = {
    New: ["To create", "bg-violet-soft text-violet border-violet/30"],
    Created: ["Created", "bg-done/10 text-done border-done/30"],
    Exists: ["Already there", "bg-surface text-muted border-line"],
    Invalid: ["Not created", "bg-hold/10 text-hold border-hold/30"],
    "Possible match": ["Check: same line?", "bg-ongoing/10 text-ongoing border-ongoing/30"],
  } as const;
  const [text, tone] = map[outcome];
  return <span className={`inline-block rounded border px-1.5 py-0.5 text-sm font-semibold whitespace-nowrap ${tone}`}>{text}</span>;
}
