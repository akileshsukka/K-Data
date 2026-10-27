import { useState, type FormEvent } from "react";
import { ApiError } from "../../api/client";
import type { AssetProfile, Proposals } from "../../api/types";
import { useMeasurements } from "../../app/useCatalog";
import { useSession } from "../../app/session";
import { Button } from "../../components/Button";
import { Field, TextInput } from "../../components/Field";
import { useToast } from "../../components/Toast";
import { dubaiLocalToUtc, formatDateTime, nowDubaiLocal } from "../../components/dates";
import { useAddReadings, useAssetReadings } from "./useAsset";

/** Record field readings (only the filled ones are saved) and see every reading with its phase. */
export function ReadingsTab({ asset, onProposals }: { asset: AssetProfile; onProposals: (p: Proposals) => void }) {
  const session = useSession();
  const measurements = useMeasurements();
  const readings = useAssetReadings(asset.id);
  const add = useAddReadings(asset.id);
  const toast = useToast();
  const [values, setValues] = useState<Record<number, string>>({});
  const [measuredAt, setMeasuredAt] = useState(nowDubaiLocal);
  const [note, setNote] = useState("");
  const canRecord = session.can("readings.write") && asset.isActive;

  const filled = Object.entries(values).filter(([, v]) => v.trim() !== "");

  async function submit(e: FormEvent) {
    e.preventDefault();
    const parsed = filled.map(([id, v]) => ({ measurementTypeId: Number(id), value: Number(v.replace(",", ".")) }));
    if (parsed.some((p) => Number.isNaN(p.value))) {
      toast("Every reading must be a number.", "error");
      return;
    }
    try {
      const r = await add.mutateAsync({ measuredAtUtc: dubaiLocalToUtc(measuredAt), note: note.trim() || null, values: parsed });
      toast(`${r.saved} reading${r.saved === 1 ? "" : "s"} saved`);
      onProposals(r.proposals);
      setValues({});
      setNote("");
      setMeasuredAt(nowDubaiLocal());
    } catch (err) {
      toast(err instanceof ApiError ? err.message : "Could not save the readings.", "error");
    }
  }

  return (
    <div className="grid gap-6 lg:grid-cols-[22rem_1fr]">
      {canRecord && (
        <form onSubmit={submit} className="h-fit rounded-lg border border-line bg-panel p-4">
          <h2 className="text-lg font-semibold">Record readings</h2>
          <p className="text-sm text-muted">Fill in only what was measured. Repeated values are fine.</p>
          <div className="mt-3 space-y-2">
            {measurements.data?.map((m) => (
              <label key={m.id} className="grid grid-cols-[1fr_7rem] items-center gap-3">
                <span className="text-[15px]">{m.label}</span>
                <TextInput
                  inputMode="decimal"
                  value={values[m.id] ?? ""}
                  onChange={(e) => setValues({ ...values, [m.id]: e.target.value })}
                  placeholder={m.unitOfMeasure ?? ""}
                  className="mt-0 text-right"
                />
              </label>
            ))}
          </div>
          <div className="mt-4 space-y-3">
            <Field label="Measured at (Dubai time)" hint="Change it when entering a reading late.">
              <TextInput type="datetime-local" value={measuredAt} max={nowDubaiLocal()} onChange={(e) => setMeasuredAt(e.target.value)} required />
            </Field>
            <Field label="Note (optional)">
              <TextInput value={note} maxLength={4000} onChange={(e) => setNote(e.target.value)} />
            </Field>
          </div>
          <Button type="submit" className="mt-4 w-full" disabled={filled.length === 0} busy={add.isPending}>
            {filled.length > 1 ? `Save ${filled.length} readings` : "Save reading"}
          </Button>
        </form>
      )}

      <section className="min-w-0">
        <h2 className="text-lg font-semibold">Reading history</h2>
        <p className="text-sm text-muted">
          {asset.preservedAtUtc ? `POST monitoring since ${formatDateTime(asset.preservedAtUtc)}.` : "Not marked as preserved yet: all readings are DURING preservation."}
        </p>
        {readings.isPending && <p className="mt-3 text-muted">Loading…</p>}
        {readings.data?.length === 0 && <p className="mt-3 text-muted">No readings yet.</p>}
        {readings.data && readings.data.length > 0 && (
          <div className="mt-3 overflow-x-auto rounded-lg border border-line bg-panel">
            <table className="w-full min-w-[34rem] border-separate border-spacing-0 text-left text-[15px]">
              <thead className="font-display text-muted">
                <tr>
                  <th className="border-b border-line px-3 py-2 font-semibold">Measured</th>
                  <th className="border-b border-line px-3 py-2 font-semibold">Measurement</th>
                  <th className="border-b border-line px-3 py-2 text-right font-semibold">Value</th>
                  <th className="border-b border-line px-3 py-2 font-semibold">Phase</th>
                  <th className="border-b border-line px-3 py-2 font-semibold">By</th>
                </tr>
              </thead>
              <tbody>
                {readings.data.map((r) => (
                  <tr key={r.id}>
                    <td className="border-b border-line px-3 py-2 whitespace-nowrap">{formatDateTime(r.measuredAtUtc)}</td>
                    <td className="border-b border-line px-3 py-2">
                      {r.label}
                      {r.note && <span className="block text-sm text-muted">{r.note}</span>}
                    </td>
                    <td className="border-b border-line px-3 py-2 text-right font-semibold tabular-nums">{r.value}</td>
                    <td className="border-b border-line px-3 py-2">
                      <span className={`rounded px-1.5 py-0.5 text-sm font-semibold ${r.phase === "Post" ? "bg-done/12 text-done" : "bg-surface text-muted"}`}>
                        {r.phase === "Post" ? "POST" : "DURING"}
                      </span>
                    </td>
                    <td className="border-b border-line px-3 py-2 text-sm text-muted">{r.recordedBy}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </section>
    </div>
  );
}
