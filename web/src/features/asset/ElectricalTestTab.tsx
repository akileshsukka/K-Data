import { useState } from "react";
import { ApiError } from "../../api/client";
import { Perm, type AssetProfile, type ElectricalBlock } from "../../api/types";
import { useSession } from "../../app/session";
import { useElectricalTestSpec } from "../../app/useCatalog";
import { Button } from "../../components/Button";
import { Dialog } from "../../components/Dialog";
import { TextInput } from "../../components/Field";
import { gatePrefix } from "../../components/GateStatus";
import { useToast } from "../../components/Toast";
import { formatDateTime } from "../../components/dates";
import { useSaveElectricalTest } from "./useAsset";

/**
 * Four colours only: winding (yellow), insulation (blue), the four DAR tests (green) and the six optional PI tests (violet).
 * The DAR and PI tests are small cards inside their coloured group.
 */
const groups = [
  { key: "winding", title: null, tone: "border-[#e6d600] bg-[#fff9c4]", match: (k: string) => k === "Winding" },
  { key: "insulation", title: null, tone: "border-[#9dc3e6] bg-[#deebf7]", match: (k: string) => k === "Insulation" },
  { key: "dar", title: "DAR tests (GΩ)", tone: "border-[#8fc46a] bg-[#e2f0d9]", match: (k: string) => k.startsWith("Dar") },
  { key: "pi", title: "PI tests (GΩ)", tone: "border-[#b4a0d9] bg-[#ece6f6]", match: (k: string) => k.startsWith("Pi") },
] as const;

type Form = { required: boolean; testedOn: string; values: Record<string, string> };

const toText = (v: number | null | undefined) => (v === null || v === undefined ? "" : String(v));
const toNumber = (s: string) => (s.trim() === "" ? null : Number(s.trim().replace(",", ".")));

/** Ratio of a DAR / PI block, as the server calculates it: second value ÷ first value, cut (not rounded) after 2 decimals. */
function ratio(block: ElectricalBlock, values: Record<string, string>) {
  if (!block.ratioLabel) return null;
  const first = toNumber(values[block.measures[0].key] ?? "");
  const second = toNumber(values[block.measures[1].key] ?? "");
  if (first === null || second === null || Number.isNaN(first) || Number.isNaN(second) || first <= 0) return null;
  // toPrecision first: 1.15 * 100 is 114.99999… in floating point and must still give 1.15.
  return Math.trunc(Number(((second / first) * 100).toPrecision(12))) / 100;
}

/**
 * Step 7 electrical test (motors): the values of the client sheet in the same blocks and colours, ratios calculated.
 * The test is optional per asset: it is activated where needed. Partial tests can be saved; the PI block is optional.
 * Once the test is complete its values are locked: a retest goes through "Register a new test", which starts from an
 * empty form and replaces the current test when saved (one test per asset).
 */
export function ElectricalTestTab({ asset }: { asset: AssetProfile }) {
  const test = asset.electricalTest ?? null;
  // A fresh form whenever the stored test changes (after a save or an Excel upload).
  return <ElectricalTestForm key={test?.updatedAtUtc ?? "none"} asset={asset} />;
}

function ElectricalTestForm({ asset }: { asset: AssetProfile }) {
  const test = asset.electricalTest ?? null;
  const spec = useElectricalTestSpec();
  const session = useSession();
  const toast = useToast();
  const save = useSaveElectricalTest(asset.id);
  const step = `${gatePrefix(asset.type)}7`;
  const canEdit = session.can(Perm.gatesEdit) && (asset.isActive || session.can("assets.manage"));
  const initial: Form = {
    required: test?.required ?? false,
    testedOn: test?.testedOn ?? "",
    values: Object.fromEntries(Object.entries(test?.values ?? {}).map(([k, v]) => [k, toText(v)])),
  };
  const [form, setForm] = useState<Form>(initial);
  const [error, setError] = useState<string | null>(null);
  // "Register a new test": empty form that replaces the stored test on save. Cancel brings the stored test back.
  const [replacing, setReplacing] = useState(false);
  const [confirmNew, setConfirmNew] = useState(false);

  if (spec.isPending) return <p className="text-muted">Loading…</p>;
  if (spec.error) return <p className="text-hold">{spec.error.message}</p>;
  const blocks = spec.data ?? [];
  const measures = blocks.flatMap((b) => b.measures);
  const bad = measures.filter((m) => { const n = toNumber(form.values[m.key] ?? ""); return n !== null && (Number.isNaN(n) || n < 0); });
  const missing = measures.filter((m) => m.required && toNumber(form.values[m.key] ?? "") === null);
  const dirty = JSON.stringify(form) !== JSON.stringify(initial);
  const filledCount = measures.filter((m) => toNumber(form.values[m.key] ?? "") !== null).length;
  // A complete test is locked; an incomplete one stays editable so it can be finished.
  const locked = !!test?.complete && !replacing;
  const editable = canEdit && !locked;

  // Plain functions, not components: a component defined here would remount (and lose the focus) on every keystroke.
  const blockLegend = (b: ElectricalBlock) => (
    <legend className="px-1 font-semibold text-ink">
      {b.title}{b.unit ? ` (${b.unit})` : ""}{!b.required && <span className="ml-2 text-sm font-normal text-muted">optional</span>}
    </legend>
  );

  const blockInputs = (b: ElectricalBlock) => {
    const r = ratio(b, form.values);
    return (
      <div className="flex flex-wrap items-end gap-3">
        {b.measures.map((m) => (
          <label key={m.key} className="block w-28">
            <span className="text-sm text-ink">{m.label.replace(`${b.title} `, "").replace(/^(Winding|IR|PI) /, "")}</span>
            <TextInput inputMode="decimal" value={form.values[m.key] ?? ""} disabled={!editable} aria-invalid={bad.includes(m)}
              aria-label={m.label} onChange={(e) => setForm({ ...form, values: { ...form.values, [m.key]: e.target.value } })} />
          </label>
        ))}
        {b.ratioLabel && (
          <div className="w-24">
            <span className="text-sm text-muted">{b.ratioLabel}</span>
            <p className="mt-1 flex min-h-10 items-center rounded-md border border-dashed border-line px-3 font-semibold">{r ?? "—"}</p>
          </div>
        )}
      </div>
    );
  };

  async function submit(next: Form = form) {
    setError(null);
    if (bad.length > 0) { setError(`${bad[0].label} must be a number (0 or more).`); return; }
    try {
      await save.mutateAsync({
        required: next.required,
        testedOn: next.testedOn || null,
        values: Object.fromEntries(measures.map((m) => [m.key, toNumber(next.values[m.key] ?? "")])),
        expected: test?.updatedAtUtc ?? null,
      });
      toast(`${step} electrical test saved`);
    } catch (err) {
      setError(err instanceof ApiError ? err.message : "Could not save.");
    }
  }

  if (!form.required && !test?.required) {
    return (
      <section className="max-w-2xl rounded-lg border border-line bg-panel p-5">
        <h2 className="text-lg font-semibold">{step} electrical test</h2>
        <p className="mt-1 text-muted">
          Not required for this asset. Activate it for motors that need the {step} test (winding resistance, insulation resistance, DAR and the optional PI).
        </p>
        {canEdit && (
          <Button className="mt-3" busy={save.isPending} onClick={() => { const next = { ...form, required: true }; setForm(next); void submit(next); }}>
            Activate {step} test
          </Button>
        )}
        {error && <p className="mt-2 text-hold">{error}</p>}
      </section>
    );
  }

  return (
    <div className="space-y-4">
      {replacing && test && (
        <p className="rounded-lg border border-ongoing/30 bg-ongoing/10 px-4 py-3 text-ink">
          New test. The current test{test.testedOn ? ` of ${test.testedOn}` : ""} (saved by {test.updatedBy}, {formatDateTime(test.updatedAtUtc)}) will be deleted when you save this one.
        </p>
      )}
      <div className="flex flex-wrap items-end justify-between gap-4 rounded-lg border border-line bg-panel px-4 py-3">
        <div>
          <h2 className="text-lg font-semibold">{step} electrical test{replacing && " · new test"}</h2>
          <p className={missing.length === 0 ? "text-done" : "text-ongoing"}>
            {missing.length === 0 ? "Complete: every required value is recorded." : `${missing.length} required value${missing.length === 1 ? "" : "s"} missing.`}
            {!form.required && <span className="ml-1 text-muted">Not required any more (the values are kept).</span>}
          </p>
          {locked && <p className="text-sm text-muted">Test recorded: the values are locked. To repeat the test, register a new one.</p>}
          {test && !replacing && <p className="text-sm text-muted">Last saved by {test.updatedBy}, {formatDateTime(test.updatedAtUtc)}</p>}
        </div>
        <div className="flex flex-wrap items-end gap-4">
          <label className="block">
            <span className="text-sm font-semibold text-ink">Test date</span>
            <TextInput type="date" value={form.testedOn} disabled={!editable} onChange={(e) => setForm({ ...form, testedOn: e.target.value })} className="w-44" />
          </label>
          <label className="flex min-h-10 items-center gap-2">
            <input type="checkbox" className="size-4 accent-violet" checked={form.required} disabled={!canEdit}
              onChange={(e) => setForm({ ...form, required: e.target.checked })} />
            <span>Requires the {step} test</span>
          </label>
        </div>
      </div>

      <div className="space-y-3">
        {groups.map((g) => {
          const inGroup = blocks.filter((b) => g.match(b.key));
          if (inGroup.length === 0) return null;
          // Winding and insulation: one block, shown directly. DAR and PI: a coloured group of small cards.
          if (!g.title) return inGroup.map((b) => (
            <fieldset key={b.key} className={`rounded-lg border-2 px-4 py-3 ${g.tone}`}>
              {blockLegend(b)}
              {blockInputs(b)}
            </fieldset>
          ));
          return (
            <fieldset key={g.key} className={`rounded-lg border-2 px-4 py-3 ${g.tone}`}>
              <legend className="px-1 font-semibold text-ink">
                {g.title}{inGroup.every((b) => !b.required) && <span className="ml-2 text-sm font-normal text-muted">optional</span>}
              </legend>
              <div className="grid gap-3 sm:grid-cols-2 2xl:grid-cols-3">
                {inGroup.map((b) => (
                  <div key={b.key} className="rounded-md border border-line/70 bg-panel/80 px-3 py-2">
                    <p className="font-semibold text-ink">{b.title.replace(/^PI /, "")}</p>
                    {blockInputs(b)}
                  </div>
                ))}
              </div>
            </fieldset>
          );
        })}
      </div>

      {canEdit && (
        <div className="flex flex-wrap items-center justify-end gap-3">
          {error && <p className="mr-auto text-hold">{error}</p>}
          {locked && (
            <Button variant="secondary" disabled={save.isPending} onClick={() => setConfirmNew(true)}>Register a new test</Button>
          )}
          {replacing ? (
            <Button variant="secondary" disabled={save.isPending} onClick={() => { setForm(initial); setReplacing(false); setError(null); }}>Cancel new test</Button>
          ) : (
            (!locked || dirty) && (
              <Button variant="secondary" disabled={!dirty || save.isPending} onClick={() => { setForm(initial); setError(null); }}>Undo changes</Button>
            )
          )}
          {(!locked || dirty) && (
            <Button busy={save.isPending} disabled={replacing ? filledCount === 0 : !dirty} onClick={() => void submit()}>
              {replacing ? "Save new test" : "Save test"}
            </Button>
          )}
        </div>
      )}

      <Dialog open={confirmNew} title="Register a new test?" onClose={() => setConfirmNew(false)}>
        <p>
          Only one {step} test is kept per asset. The current test{test?.testedOn ? ` of ${test.testedOn}` : ""} and all its values will be
          deleted and replaced by the new one when you save it.
        </p>
        <p className="mt-2 text-sm text-muted">Nothing is deleted until you save. You can still cancel the new test before that.</p>
        <div className="mt-5 flex justify-end gap-3">
          <Button variant="secondary" onClick={() => setConfirmNew(false)}>Cancel</Button>
          <Button variant="danger" onClick={() => {
            setForm({ required: form.required, testedOn: "", values: {} });
            setReplacing(true);
            setError(null);
            setConfirmNew(false);
          }}>Start new test</Button>
        </div>
      </Dialog>
    </div>
  );
}
