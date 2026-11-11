import type { RegisterRow } from "../../api/types";

/** Shown when the lines of a group do not have the same value (same word as in the grouped Excel). */
export const MIXED = "Mixed";

/** Sections of one line (same unit, sequence and service; size, spec and insulation may differ), shown as one row. Nothing is stored: worked out from the line numbers. */
export interface LineGroup {
  key: string;
  /** The lines of the group that pass the filters, by size. */
  members: RegisterRow[];
  /** One row with the values of the group: a value all lines have, otherwise {@link MIXED}. */
  row: RegisterRow;
}

const norm = (v: string | null | undefined) => (v ?? "").replace(/\s+/g, " ").trim().toLowerCase();

/** The value when every line has it (empty counts), otherwise Mixed. */
export function common(values: (string | null | undefined)[]): string | null {
  if (values.length === 0) return null;
  const first = values[0] ?? null;
  return values.every((v) => norm(v) === norm(first)) ? first : MIXED;
}

/** P&ID numbers of the lines listed together, without repeats. */
function joined(values: (string | null | undefined)[]): string | null {
  const parts: string[] = [];
  for (const v of values) for (const p of (v ?? "").split(/[,\n]/).map((s) => s.trim()).filter(Boolean)) if (!parts.some((x) => norm(x) === norm(p))) parts.push(p);
  return parts.length ? parts.join(", ") : null;
}

function merge(members: RegisterRow[]): RegisterRow {
  if (members.length === 1) return members[0];
  const first = members[0];
  const pick = <T,>(f: (r: RegisterRow) => T) => members.map(f);
  const customKeys = [...new Set(members.flatMap((m) => Object.keys(m.custom ?? {})))];
  return {
    ...first,
    tag: members.map((m) => m.tag ?? m.code).join("\n"),
    currentGate: common(pick((r) => r.currentGate)) ?? first.currentGate,
    gates: first.gates.map((_, i) => common(pick((r) => r.gates[i]))),
    emWalkdown: common(pick((r) => r.emWalkdown)),
    actionBy: common(pick((r) => r.actionBy)),
    currentActivity: common(pick((r) => r.currentActivity)),
    bulkLiquidDrained: common(pick((r) => r.bulkLiquidDrained)),
    circuit: common(pick((r) => r.circuit)),
    custom: Object.fromEntries(customKeys.map((k) => [k, common(pick((r) => r.custom?.[k]))])),
    documentsPending: members.reduce((s, r) => s + r.documentsPending, 0),
    documentsDone: members.reduce((s, r) => s + (r.documentsDone ?? 0), 0),
    isActive: members.some((r) => r.isActive),
    groups: [...new Set(members.flatMap((r) => r.groups ?? []))],
    line: first.line && {
      ...first.line,
      sizeIn: null,
      service: common(pick((r) => r.line?.service)),
      pidNumber: joined(pick((r) => r.line?.pidNumber)),
      lengthM: members.reduce((s, r) => s + (r.line?.lengthM ?? 0), 0) || null,
      numberCheck: members.find((r) => r.line?.numberCheck?.nonStandard)?.line?.numberCheck ?? first.line.numberCheck,
    },
  };
}

/** Lines in size groups, in the order of the list (where the first line of the group is); inside a group by size. */
export function groupLines(rows: RegisterRow[]): LineGroup[] {
  const groups = new Map<string, RegisterRow[]>();
  rows.forEach((r, i) => {
    const key = r.line?.groupKey ?? `#${r.id}-${i}`;
    const list = groups.get(key);
    if (list) list.push(r);
    else groups.set(key, [r]);
  });
  return [...groups.entries()].map(([key, list]) => {
    const members = [...list].sort((a, b) => (a.line?.sizeIn ?? Infinity) - (b.line?.sizeIn ?? Infinity));
    return { key, members, row: merge(members) };
  });
}
