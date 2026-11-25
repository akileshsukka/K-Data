import { ApiError } from "../../api/client";
import { Perm, type AssetProfile } from "../../api/types";
import { useSession } from "../../app/session";
import { useToast } from "../../components/Toast";
import { formatDate } from "../../components/dates";
import { useEffectiveOn, useSetEffectiveOn } from "./useAsset";

/**
 * Effective on: the gate changes this asset had today count in today's daily report unless the data engineer or an
 * admin says the work was done yesterday. Shown only to them and only while the asset has gate changes recorded today.
 */
export function EffectiveOnCard({ asset, className = "" }: { asset: AssetProfile; className?: string }) {
  const session = useSession();
  const can = session.can(Perm.assetsEffectiveOn);
  const state = useEffectiveOn(asset.id, can);
  const set = useSetEffectiveOn(asset.id);
  const toast = useToast();
  const s = state.data;
  if (!can || !s || s.changes === 0) return null;

  async function choose(day: "Today" | "Yesterday") {
    try {
      await set.mutateAsync(day);
      toast(day === "Yesterday" ? "Today's gate changes count in yesterday's daily report" : "Today's gate changes count today");
    } catch (err) {
      toast(err instanceof ApiError ? err.message : "Could not save.", "error");
    }
  }

  const options = [
    { day: "Yesterday" as const, date: s.yesterday },
    { day: "Today" as const, date: s.today },
  ];
  const partly = s.moved > 0 && s.moved < s.changes;

  return (
    <section className={`rounded-lg border border-line bg-panel p-4 ${className}`}>
      <h2 className="text-lg font-semibold">Effective on</h2>
      <p className="mt-1 text-[15px] text-muted">
        {s.changes} gate change{s.changes === 1 ? "" : "s"} recorded today. Daily report day:
      </p>
      <div className="mt-2 flex flex-wrap gap-2" role="group" aria-label="Effective on">
        {options.map((o) => {
          const active = s.effectiveOn === o.day;
          return (
            <button
              key={o.day}
              type="button"
              aria-pressed={active}
              disabled={set.isPending}
              onClick={() => !active && choose(o.day)}
              className={`min-h-10 rounded-md border px-4 font-semibold ${active ? "border-violet bg-violet text-white" : "border-line bg-panel text-ink hover:border-violet"}`}
            >
              {o.day} <span className="font-normal">· {formatDate(o.date)}</span>
            </button>
          );
        })}
      </div>
      {partly && (
        <p className="mt-2 text-sm text-ongoing">
          {s.changes - s.moved} later change{s.changes - s.moved === 1 ? "" : "s"} of today still count{s.changes - s.moved === 1 ? "s" : ""} today.{" "}
          <button type="button" className="font-semibold text-violet" disabled={set.isPending} onClick={() => choose("Yesterday")}>Move to yesterday too</button>
        </p>
      )}
    </section>
  );
}
