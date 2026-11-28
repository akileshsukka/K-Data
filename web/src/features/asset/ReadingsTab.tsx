import { useState, type FormEvent } from "react";
import { ApiError } from "../../api/client";
import { Perm, type AssetProfile, type Proposals, type Reading } from "../../api/types";
import { useMeasurements } from "../../app/useCatalog";
import { useSession } from "../../app/session";
import { Button } from "../../components/Button";
import { Field, TextInput } from "../../components/Field";
import { useToast } from "../../components/Toast";
import { Dialog } from "../../components/Dialog";
import { dubaiLocalToUtc, formatDateTime, nowDubaiLocal, utcToDubaiLocal } from "../../components/dates";
import { useAddReadings, useAssetReadings, useCorrectReading, useRemoveReading } from "./useAsset";

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
  const canCorrect = session.can(Perm.readingsCorrect) && asset.isActive;
  const [correcting, setCorrecting] = useState<Reading | null>(null);

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
                  {canCorrect && <th className="border-b border-line px-3 py-2"><span className="sr-only">Correct</span></th>}
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
                    {canCorrect && (
                      <td className="border-b border-line px-2 py-1 text-right">
                        <Button variant="quiet" className="min-h-8 px-2 text-sm" onClick={() => setCorrecting(r)}>
                          Correct
                        </Button>
                      </td>
                    )}
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </section>
      {correcting && <CorrectReadingDialog assetId={asset.id} reading={correcting} onClose={() => setCorrecting(null)} />}
    </div>
  );
}

/**
 * Admin correction of a reading typed with a wrong date or value, or removal of one entered by mistake.
 * The reading as it was and the reason stay in the asset history (internal: only Engineer and Admin see it).
 */
function CorrectReadingDialog({ assetId, reading, onClose }: { assetId: string; reading: Reading; onClose: () => void }) {
  const correct = useCorrectReading(assetId);
  const remove = useRemoveReading(assetId);
  const toast = useToast();
  const original = utcToDubaiLocal(reading.measuredAtUtc);
  const [measuredAt, setMeasuredAt] = useState(original);
  const [value, setValue] = useState(String(reading.value));
  const [reason, setReason] = useState("");
  const [confirmRemove, setConfirmRemove] = useState(false);
  const expected = { measuredAtUtc: reading.measuredAtUtc, value: reading.value };
  const pressure = reading.measurement === "PressureBarg" || reading.measurement === "G4N2BlanketBarg";

  async function save(e: FormEvent) {
    e.preventDefault();
    const parsed = Number(value.replace(",", "."));
    if (value.trim() === "" || Number.isNaN(parsed)) {
      toast("The value must be a number.", "error");
      return;
    }
    try {
      await correct.mutateAsync({
        readingId: reading.id,
        // Same minute as before: the exact original time is kept (the field has no seconds).
        measuredAtUtc: measuredAt === original ? reading.measuredAtUtc : dubaiLocalToUtc(measuredAt),
        value: parsed,
        reason: reason.trim(),
        expected,
      });
      toast("Reading corrected");
      onClose();
    } catch (err) {
      toast(err instanceof ApiError ? err.message : "Could not correct the reading.", "error");
    }
  }

  async function removeReading() {
    try {
      await remove.mutateAsync({ readingId: reading.id, reason: reason.trim(), expected });
      toast("Reading removed");
      onClose();
    } catch (err) {
      toast(err instanceof ApiError ? err.message : "Could not remove the reading.", "error");
    }
  }

  return (
    <Dialog open title={`Correct ${reading.label}`} onClose={onClose}>
      <form onSubmit={save} className="space-y-3">
        <p className="text-sm text-muted">
          Entered by {reading.recordedBy} on {formatDateTime(reading.recordedAtUtc)}: {reading.value}
          {reading.unitOfMeasure ? ` ${reading.unitOfMeasure}` : ""} measured {formatDateTime(reading.measuredAtUtc)}. The reading as it is now and
          the reason stay in the asset history, seen only by Engineer and Admin. It cannot be dated after it was entered.
        </p>
        <Field label="Measured at (Dubai time)">
          <TextInput type="datetime-local" value={measuredAt} max={utcToDubaiLocal(reading.recordedAtUtc)} onChange={(e) => setMeasuredAt(e.target.value)} required />
        </Field>
        <Field label={`Value${reading.unitOfMeasure ? ` (${reading.unitOfMeasure})` : ""}`}>
          <TextInput inputMode="decimal" value={value} onChange={(e) => setValue(e.target.value)} required />
        </Field>
        <Field label="Reason" hint="Required. For example: typed with the wrong date.">
          <TextInput value={reason} maxLength={500} onChange={(e) => setReason(e.target.value)} required />
        </Field>
        {pressure && (
          <p className="text-sm text-muted">
            A pressure of a round moves between G4 - N2 Blanket and Pressure (barg) if the new date falls on the other side of the preservation date.
          </p>
        )}
        {confirmRemove ? (
          <div className="rounded-md border border-hold/40 p-3">
            <p className="text-[15px]">Remove this reading? The asset values are worked out again from the other readings.</p>
            <div className="mt-3 flex justify-end gap-2">
              <Button type="button" variant="secondary" onClick={() => setConfirmRemove(false)}>
                Keep it
              </Button>
              <Button type="button" variant="danger" disabled={reason.trim() === ""} busy={remove.isPending} onClick={removeReading}>
                Remove reading
              </Button>
            </div>
          </div>
        ) : (
          <div className="flex flex-wrap justify-between gap-2 pt-2">
            <Button type="button" variant="danger" onClick={() => setConfirmRemove(true)}>
              Remove…
            </Button>
            <div className="flex gap-2">
              <Button type="button" variant="secondary" onClick={onClose}>
                Cancel
              </Button>
              <Button type="submit" disabled={reason.trim() === ""} busy={correct.isPending}>
                Save correction
              </Button>
            </div>
          </div>
        )}
      </form>
    </Dialog>
  );
}
