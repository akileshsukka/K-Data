import type { GateDocStatus, RegisterRow } from "../../api/types";

/**
 * "As the client": the parts of a split item (P-101A, P-101B of the client's P-101A/B) shown as one row, with the same
 * values as its row in the client Excel (GroupedRows on the server):
 * - statuses: the most advanced — Completed (or Yes) > Ongoing > other values > Hold (or No) > empty; N/A only when all parts are N/A;
 * - Current Gate: the highest (Removed from Site only when every part is removed);
 * - dates: the first one; text: once when the parts agree, otherwise "P-101A: … / P-101B: …".
 * Nothing is stored: each part keeps its own values and is edited on its own.
 */
export interface ClientGroup {
  key: string;
  /** The parts of the split item (one row: an asset that was never split). */
  members: RegisterRow[];
  /** One row with the values of the client Excel row. */
  row: RegisterRow;
}

const norm = (v: string | null | undefined) => (v ?? "").replace(/\s+/g, " ").trim().toLowerCase();
const isNa = (v: string | null | undefined) => ["not applicable", "n/a", "na"].includes(norm(v));

function rank(v: string | null | undefined): number {
  const s = norm(v);
  if (!s) return 0;
  if (s.startsWith("completed") || s === "yes") return 4;
  if (s === "ongoing") return 3;
  if (s === "hold" || s === "no") return 1;
  return 2;
}

/** The most advanced status of the parts; between equal ranks, the first part's. */
export function status(values: (string | null | undefined)[]): string | null {
  const applicable = values.filter((v) => !isNa(v));
  if (applicable.length === 0) return values[0] ?? null;
  return applicable.reduce<string | null>((best, v) => (rank(v) > rank(best) ? (v ?? null) : best), applicable[0] ?? null);
}

const gateOrder = (g: string) => (g === "New" ? 0 : g === "Removed from Site" ? -1 : Number(g.slice(1)) || 0);

/** The highest Current Gate; Removed from Site only when every part is removed. */
export function highestGate(values: string[]): string {
  const onSite = values.filter((g) => g !== "Removed from Site");
  if (onSite.length === 0) return values[0];
  return onSite.reduce((best, g) => (gateOrder(g) > gateOrder(best) ? g : best), onSite[0]);
}

/** Text once when the parts agree; otherwise each part's own, labelled with its tag. */
export function text(members: RegisterRow[], pick: (r: RegisterRow) => string | null | undefined): string | null {
  const said = members.map((m) => ({ tag: m.tag ?? m.code, value: pick(m)?.trim() || null })).filter((x) => x.value);
  if (said.length === 0) return null;
  if (said.every((x) => x.value === said[0].value)) return said[0].value;
  return said.map((x) => `${x.tag}: ${x.value}`).join(" / ");
}

const first = (values: (string | null | undefined)[]) => values.filter((v): v is string => !!v).sort()[0] ?? null;

/** A gate whose documentation is pending on any part is pending; otherwise the first part's state. */
function docStatus(values: (GateDocStatus | null | undefined)[]): GateDocStatus | null {
  if (values.includes("Pending")) return "Pending";
  return values.find((v) => !!v) ?? null;
}

function merge(members: RegisterRow[]): RegisterRow {
  if (members.length === 1) return members[0];
  const [head] = members;
  const pick = <T,>(f: (r: RegisterRow) => T) => members.map(f);
  const customKeys = [...new Set(members.flatMap((m) => Object.keys(m.custom ?? {})))];
  return {
    ...head,
    tag: head.clientTag ?? head.tag,
    description: text(members, (r) => r.description),
    currentGate: highestGate(pick((r) => r.currentGate)),
    gates: head.gates.map((_, i) => status(pick((r) => r.gates[i]))),
    gateDocuments: head.gateDocuments?.map((_, i) => docStatus(pick((r) => r.gateDocuments?.[i]))) ?? head.gateDocuments,
    assetAvailability: status(pick((r) => r.assetAvailability)),
    preservationStatus: status(pick((r) => r.preservationStatus)),
    thirdPartyInspection: status(pick((r) => r.thirdPartyInspection)),
    emWalkdown: status(pick((r) => r.emWalkdown)),
    preservedByEu: status(pick((r) => r.preservedByEu)),
    actionBy: status(pick((r) => r.actionBy)),
    n2BlanketStatus: status(pick((r) => r.n2BlanketStatus)),
    currentActivity: status(pick((r) => r.currentActivity)),
    readyForPreservation: status(pick((r) => r.readyForPreservation)),
    preservationRemarks: text(members, (r) => r.preservationRemarks),
    lastFieldActionDate: first(pick((r) => r.lastFieldActionDate)),
    preservedAtUtc: first(pick((r) => r.preservedAtUtc)),
    custom: Object.fromEntries(customKeys.map((k) => [k, text(members, (r) => r.custom?.[k])])),
    documentsPending: members.reduce((s, r) => s + r.documentsPending, 0),
    documentsDone: members.reduce((s, r) => s + (r.documentsDone ?? 0), 0),
    e7Tested: head.e7Tested == null ? head.e7Tested : members.every((r) => !!r.e7Tested),
    isActive: members.some((r) => r.isActive),
    groups: [...new Set(members.flatMap((r) => r.groups ?? []))],
  };
}

/** Rows as the client lists them, in the order of the register (a split item where its first part is). */
export function groupByClientTag(rows: RegisterRow[]): ClientGroup[] {
  const groups = new Map<string, RegisterRow[]>();
  rows.forEach((r, i) => {
    const key = r.clientTag ? `client:${norm(r.clientTag).replace(/\s/g, "")}` : `#${r.id}-${i}`;
    const list = groups.get(key);
    if (list) list.push(r);
    else groups.set(key, [r]);
  });
  return [...groups.entries()].map(([key, members]) => ({ key, members, row: merge(members) }));
}
