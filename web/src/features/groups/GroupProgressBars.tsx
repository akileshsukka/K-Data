import type { GroupProgress } from "../../api/types";

const names = ["G1 Product drain", "G2 Dew point", "G3 O2", "G4 N2 blanketing", "G5", "G6", "G7", "G8"];

/** Completed share of each gate for the active members (only gates someone started are shown after G4). */
export function GroupProgressBars({ progress, compact = false }: { progress: GroupProgress; compact?: boolean }) {
  const total = progress.members - progress.removedFromSite;
  const shown = progress.gatesCompleted.map((done, i) => ({ i, done })).filter((g) => g.i < 4 || g.done > 0);
  if (total <= 0) return null;
  return (
    <dl className={`mt-3 grid gap-x-4 gap-y-1.5 ${compact ? "grid-cols-2 sm:grid-cols-4" : "sm:grid-cols-2"}`}>
      {shown.map(({ i, done }) => {
        const pct = Math.round((done / total) * 100);
        return (
          <div key={i} className="min-w-0">
            <dt className="flex justify-between gap-2 text-sm text-muted">
              <span className="truncate">{compact ? `G${i + 1}` : names[i]}</span>
              <span className="tabular-nums">{done}/{total}</span>
            </dt>
            <dd className="mt-0.5 h-2 overflow-hidden rounded-full bg-surface" role="img" aria-label={`G${i + 1}: ${pct}% completed`}>
              <span className="block h-full rounded-full bg-violet" style={{ width: `${pct}%` }} />
            </dd>
          </div>
        );
      })}
    </dl>
  );
}
