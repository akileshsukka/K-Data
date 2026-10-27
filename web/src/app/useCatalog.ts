import { useQuery } from "@tanstack/react-query";
import { api } from "../api/client";
import type { AssetType, CustomField, Discipline, FieldDef, LineCode, LineCodeKind, Measurement, OptionSet, PipeSpec, Plant, Unit } from "../api/types";

const LONG = 30 * 60_000;

export function usePlants() {
  return useQuery({ queryKey: ["catalog", "plants"], queryFn: ({ signal }) => api<Plant[]>("/api/catalog/plants", { signal }), staleTime: LONG });
}

export function useFields() {
  return useQuery({ queryKey: ["catalog", "fields"], queryFn: ({ signal }) => api<FieldDef[]>("/api/catalog/fields", { signal }), staleTime: Infinity });
}

export function useOptions() {
  return useQuery({
    queryKey: ["catalog", "options"],
    queryFn: ({ signal }) => api<OptionSet[]>("/api/catalog/options", { signal }),
    staleTime: LONG,
    select: (sets) => Object.fromEntries(sets.map((s) => [s.key, s.values])) as Record<string, string[]>,
  });
}

export function useDisciplines() {
  return useQuery({ queryKey: ["catalog", "disciplines"], queryFn: ({ signal }) => api<Discipline[]>("/api/catalog/disciplines", { signal }), staleTime: LONG });
}

export function useMeasurements() {
  return useQuery({ queryKey: ["catalog", "measurements"], queryFn: ({ signal }) => api<Measurement[]>("/api/catalog/measurements", { signal }), staleTime: LONG });
}

export interface LineCodeBook {
  /** Meaning of a code, or undefined when it is not in the list (or inactive). */
  describe: (kind: LineCodeKind, code: string | null | undefined) => string | undefined;
  all: LineCode[];
}

/** Service and insulation codes of the line numbers (admin-editable list). */
export function useLineCodes(): LineCodeBook {
  const q = useQuery({ queryKey: ["catalog", "line-codes"], queryFn: ({ signal }) => api<LineCode[]>("/api/catalog/line-codes", { signal }), staleTime: LONG });
  const all = q.data ?? [];
  const map = new Map(all.filter((c) => c.isActive).map((c) => [`${c.kind}:${c.code}`, c.description]));
  const describe = (kind: LineCodeKind, code: string | null | undefined) => (code ? map.get(`${kind}:${code.toUpperCase()}`) : undefined);
  return { describe, all };
}

/** Pipe spec catalogue: class and flange type of a spec code (active specs only). */
export function usePipeSpecs() {
  const q = useQuery({ queryKey: ["catalog", "pipe-specs"], queryFn: ({ signal }) => api<PipeSpec[]>("/api/catalog/pipe-specs", { signal }), staleTime: LONG });
  const all = q.data ?? [];
  const map = new Map(all.filter((s) => s.isActive).map((s) => [s.code.toUpperCase(), s]));
  return { all, isPending: q.isPending, find: (code: string | null | undefined) => (code ? map.get(code.toUpperCase()) : undefined) };
}

export function useUnit(unitId: number | undefined): { unit?: Unit; plantName?: string; isPending: boolean } {
  const plants = usePlants();
  for (const plant of plants.data ?? []) {
    const unit = plant.units.find((u) => u.id === unitId);
    if (unit) return { unit, plantName: plant.name, isPending: false };
  }
  return { isPending: plants.isPending };
}

const LAST_UNIT_KEY = "presapp.lastUnit";

/** Remembers the last unit on this device only (a convenience; nothing depends on it). */
export const lastUnit = {
  get(): number | undefined {
    try {
      const v = Number(localStorage.getItem(LAST_UNIT_KEY));
      return Number.isFinite(v) && v > 0 ? v : undefined;
    } catch {
      return undefined;
    }
  },
  set(id: number) {
    try {
      localStorage.setItem(LAST_UNIT_KEY, String(id));
    } catch {
      /* storage unavailable: ignore */
    }
  },
};

/** Active custom fields of one register (or all of them with <paramref name="all"/>). */
export function useCustomFields(type?: AssetType, all = false) {
  return useQuery({
    queryKey: ["catalog", "custom-fields", type ?? "any", all],
    queryFn: ({ signal }) => api<CustomField[]>(`/api/catalog/custom-fields?${type ? `type=${type}&` : ""}all=${all}`, { signal }),
    staleTime: LONG,
  });
}

