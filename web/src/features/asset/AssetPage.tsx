import { useState } from "react";
import { Link, useParams, useSearchParams } from "react-router";
import { ApiError } from "../../api/client";
import type { AssetProfile, Proposals } from "../../api/types";
import { CustomFieldsCards } from "./CustomFieldsCard";
import { useFields, useOptions, useLineCodes, usePipeSpecs } from "../../app/useCatalog";
import { useSession } from "../../app/session";
import { Button } from "../../components/Button";
import { TextInput } from "../../components/Field";
import { CurrentGateChip } from "../../components/GateStatus";
import { useToast } from "../../components/Toast";
import { formatDate, formatDateTime, todayDubai } from "../../components/dates";
import { FieldSection } from "./FieldSection";
import { GatesTab } from "./GatesTab";
import { HistoryList } from "./HistoryList";
import { ReadingsTab } from "./ReadingsTab";
import { CategoryCard } from "./CategoryCard";
import { useAsset, useAssetHistory, useMarkPreserved, usePatchAsset, useSetActive, useSetCurrentGate } from "./useAsset";

const tabs = [
  { key: "overview", label: "Overview" },
  { key: "gates", label: "Gates" },
  { key: "readings", label: "Readings" },
  { key: "history", label: "History" },
] as const;

/** Everything about one asset: overview, gates with documentation, readings and history. */
export function AssetPage() {
  const id = useParams().id!;
  const [params, setParams] = useSearchParams();
  const tab = params.get("tab") ?? "overview";
  const asset = useAsset(id);
  const fields = useFields();
  const options = useOptions();
  const [proposals, setProposals] = useState<Proposals | null>(null);

  if (asset.isPending || fields.isPending || options.isPending) return <p className="text-muted">Loading…</p>;
  if (asset.error) return <p className="text-hold">{asset.error.message}</p>;
  const a = asset.data;
  // Lines use their own lists (e.g. piping Current Activity, several values allowed).
  const defs = (fields.data ?? []).map((f) =>
    a.type === "Pipeline" && f.pipelineOptionSet ? { ...f, optionSet: f.pipelineOptionSet, multiple: !!f.pipelineMultiple } : f,
  );
  const opts = options.data ?? {};
  const section = (name: string) => defs.filter((f) => f.section === name && (!f.onlyFor || f.onlyFor === a.type));
  const registerPath = a.type === "ElectricalInstrument" ? "ei" : a.type === "Pipeline" ? "lines" : "equipment";

  return (
    <div>
      <Link to={`/u/${a.unit.id}/${registerPath}`} className="text-sm font-semibold text-violet">
        {a.unit.name} register
      </Link>
      <header className="mt-2 flex flex-wrap items-start justify-between gap-4 border-b border-line pb-5">
        <div className="min-w-0">
          <h1 className="text-[2rem] leading-tight font-semibold break-words">{a.values.Tag ?? a.code}</h1>
          <p className="text-muted">
            {a.code} — {a.discipline.name} — {a.unit.name}
            {!a.isActive && <span className="ml-2 rounded bg-hold/10 px-1.5 py-0.5 text-sm font-semibold text-hold">Inactive</span>}
          </p>
          {a.values.Description && <p className="mt-1 max-w-2xl">{a.values.Description}</p>}
        </div>
        <div className="text-right">
          <span className="block text-sm text-muted">Current Gate</span>
          <CurrentGateChip value={a.currentGate} />
        </div>
      </header>

      <ProposalBanner asset={a} proposals={proposals} onDone={() => setProposals(null)} />

      <nav className="mt-5 flex gap-1 overflow-x-auto border-b border-line" aria-label="Asset sections">
        {tabs.map((t) => (
          <button
            key={t.key}
            onClick={() => setParams(t.key === "overview" ? {} : { tab: t.key }, { replace: true })}
            className={`-mb-px border-b-2 px-4 py-2 font-semibold whitespace-nowrap ${tab === t.key ? "border-violet text-violet" : "border-transparent text-muted hover:text-ink"}`}
          >
            {t.label}
          </button>
        ))}
      </nav>

      <div className="mt-5">
        {tab === "overview" && (
          <div className="grid gap-5 xl:grid-cols-2">
            <div className="space-y-5">
              <FieldSection title="Identification" fields={section("Identification").map((f) => (f.key === "Tag" && a.type === "Pipeline" ? { ...f, label: "Line number" } : f))} asset={a} options={opts} onProposals={setProposals} />
              {a.type === "Pipeline" && (
                <div>
                  <FieldSection title="Line" fields={section("Line")} asset={a} options={opts} onProposals={setProposals} />
                  <SpecDetails spec={a.values.PipeSpec} />
                  <LineMeaning service={a.values.Service} />
                </div>
              )}
              <FieldSection title="Status" fields={section("Status")} asset={a} options={opts} onProposals={setProposals} />
              {a.type !== "Pipeline" && <CategoryCard asset={a} />}
              <CustomFieldsCards asset={a} />
            </div>
            <div className="space-y-5">
              <FieldSection title="Client tracking" fields={section("Client tracking")} asset={a} options={opts} onProposals={setProposals} />
              <FieldSection title="Latest readings" fields={section("Latest readings")} asset={a} options={opts} onProposals={setProposals} />
              <FieldSection title="Milestones" fields={section("Milestones").filter((f) => f.key !== "PreservedAtUtc")} asset={a} options={opts} onProposals={setProposals} />
              <PreservedCard asset={a} />
              {a.groups.length > 0 && (
                <section className="rounded-lg border border-line bg-panel p-4">
                  <h2 className="text-lg font-semibold">Workpacks and groups</h2>
                  <ul className="mt-2 flex flex-wrap gap-2">
                    {a.groups.map((g) => (
                      <li key={g.id}>
                        <Link to={`/groups/${g.id}`} className="inline-flex flex-col rounded-md border border-line px-3 py-1.5 hover:border-violet">
                          <span className="font-semibold text-ink">{g.code}</span>
                          <span className="text-sm text-muted">{g.type} · {g.name}</span>
                        </Link>
                      </li>
                    ))}
                  </ul>
                </section>
              )}
              <AdminCard asset={a} />
            </div>
          </div>
        )}
        {tab === "gates" && <GatesTab asset={a} fields={defs} options={opts} onProposals={setProposals} />}
        {tab === "readings" && <ReadingsTab asset={a} onProposals={setProposals} />}
        {tab === "history" && <AssetHistory id={a.id} />}
      </div>
    </div>
  );
}

/** Derived values the user approves: Current Gate and Last Field Action Date. */
function ProposalBanner({ asset, proposals, onDone }: { asset: AssetProfile; proposals: Proposals | null; onDone: () => void }) {
  const setGate = useSetCurrentGate(asset.id);
  const patch = usePatchAsset(asset.id);
  const toast = useToast();
  if (!proposals || (!proposals.currentGate && !proposals.lastFieldActionDate)) return null;

  async function apply() {
    try {
      if (proposals!.currentGate) await setGate.mutateAsync({ value: proposals!.currentGate, expected: asset.currentGate });
      if (proposals!.lastFieldActionDate) {
        await patch.mutateAsync([{ field: "LastFieldActionDate", from: asset.values.LastFieldActionDate ?? null, to: proposals!.lastFieldActionDate }]);
      }
      toast("Updated");
      onDone();
    } catch (err) {
      toast(err instanceof ApiError ? err.message : "Could not update.", "error");
    }
  }

  return (
    <div className="mt-4 flex flex-wrap items-center gap-3 rounded-lg border border-violet/40 bg-violet-soft px-4 py-3" role="status">
      <p className="flex-1">
        {proposals.currentGate && (
          <>Current Gate should be <strong>{proposals.currentGate}</strong> (now {asset.currentGate}). </>
        )}
        {proposals.lastFieldActionDate && (
          <>Last Field Action Date can move to <strong>{formatDate(proposals.lastFieldActionDate)}</strong>.</>
        )}
      </p>
      <Button busy={setGate.isPending || patch.isPending} onClick={apply}>Apply</Button>
      <Button variant="secondary" onClick={onDone}>Keep as is</Button>
    </div>
  );
}

/** The engineer marks the official start of POST monitoring. Never automatic. */
function PreservedCard({ asset }: { asset: AssetProfile }) {
  const session = useSession();
  const mark = useMarkPreserved(asset.id);
  const toast = useToast();
  const [day, setDay] = useState<string | null>(null);
  const canMark = session.can("assets.markPreserved") && asset.isActive;

  async function save(value: string | null) {
    try {
      await mark.mutateAsync(value);
      toast(value ? `Marked as preserved from ${formatDate(value)}` : "Preserved mark removed");
      setDay(null);
    } catch (err) {
      toast(err instanceof ApiError ? err.message : "Could not save.", "error");
    }
  }

  return (
    <section className="rounded-lg border border-line bg-panel p-4">
      <h2 className="text-lg font-semibold">Preserved</h2>
      {asset.preservedAtUtc ? (
        <p className="mt-1">Preserved since <strong>{formatDateTime(asset.preservedAtUtc)}</strong> — marked by {asset.preservedBy}. Readings from then on are POST monitoring.</p>
      ) : (
        <p className="mt-1 text-muted">Not marked yet. Mark it when all gates are done and periodic monitoring starts.</p>
      )}
      {canMark && day === null && (
        <div className="mt-3 flex gap-2">
          <Button variant="secondary" onClick={() => setDay(todayDubai())}>{asset.preservedAtUtc ? "Change date" : "Mark as preserved"}</Button>
          {asset.preservedAtUtc && <Button variant="danger" busy={mark.isPending} onClick={() => save(null)}>Remove mark</Button>}
        </div>
      )}
      {day !== null && (
        <div className="mt-3 flex flex-wrap items-center gap-2">
          <TextInput type="date" value={day} max={todayDubai()} onChange={(e) => setDay(e.target.value)} aria-label="Preserved from" className="mt-0 w-44" />
          <Button busy={mark.isPending} onClick={() => save(day)}>Save</Button>
          <Button variant="secondary" onClick={() => setDay(null)}>Cancel</Button>
        </div>
      )}
    </section>
  );
}

function AdminCard({ asset }: { asset: AssetProfile }) {
  const session = useSession();
  const setActive = useSetActive(asset.id);
  const toast = useToast();
  if (!session.can("assets.manage")) return null;

  async function toggle() {
    if (asset.isActive && !window.confirm("Deactivate this asset? It disappears from the registers and the Excel. It is never deleted.")) return;
    try {
      await setActive.mutateAsync(!asset.isActive);
      toast(asset.isActive ? "Asset deactivated" : "Asset reactivated");
    } catch (err) {
      toast(err instanceof ApiError ? err.message : "Could not change it.", "error");
    }
  }

  return (
    <section className="rounded-lg border border-line bg-panel p-4">
      <h2 className="text-lg font-semibold">Administration</h2>
      <p className="mt-1 text-sm text-muted">
        {asset.simproFlangeId ? "In Simpro." : "Not in Simpro yet: it is created by the next sync."} Last change by {asset.updatedBy} on {formatDateTime(asset.updatedAtUtc)}.
      </p>
      <Button variant={asset.isActive ? "danger" : "secondary"} className="mt-3" busy={setActive.isPending} onClick={toggle}>
        {asset.isActive ? "Deactivate asset" : "Reactivate asset"}
      </Button>
    </section>
  );
}

function AssetHistory({ id }: { id: string }) {
  const history = useAssetHistory(id);
  if (history.isPending) return <p className="text-muted">Loading…</p>;
  return <HistoryList entries={history.data ?? []} />;
}

/** Rating, face and gasket of the line, from the pipe spec list (Admin → Pipe specs). */
function SpecDetails({ spec }: { spec: string | null | undefined }) {
  const specs = usePipeSpecs();
  if (!spec) return null;
  const s = specs.find(spec);
  const rows: [string, string | null | undefined][] = [["Rating", s?.pressureClass], ["Face", s?.flangeType], ["Gasket", s?.gasket]];
  return (
    <section className="mt-3 rounded-lg border border-line bg-panel">
      <header className="border-b border-line px-4 py-2.5">
        <h2 className="text-lg font-semibold">Pipe spec {spec}</h2>
        {!s && <p className="text-sm text-ongoing">Not in the pipe spec list: add it in Admin → Pipe specs to get its rating, face and gasket.</p>}
      </header>
      <dl className="grid gap-x-6 sm:grid-cols-3">
        {rows.map(([label, value]) => (
          <div key={label} className="border-b border-line/70 px-4 py-2.5 last:border-b-0 sm:border-b-0">
            <dt className="text-sm text-muted">{label}</dt>
            <dd className={`mt-0.5 ${value ? "text-text" : "text-muted/60"}`}>{value || "—"}</dd>
          </div>
        ))}
      </dl>
    </section>
  );
}

/** Meaning of the service code (Admin → Line codes). */
function LineMeaning({ service }: { service: string | null | undefined }) {
  const codes = useLineCodes();
  if (!service) return null;
  return (
    <p className="mt-2 px-1 text-sm text-muted">
      <strong className="text-ink">{service}</strong>: {codes.describe("Service", service) ?? "not in the line codes"}
    </p>
  );
}
