import { useQuery } from "@tanstack/react-query";
import { api } from "../api/client";
import type { Plant, Unit } from "../api/types";

export function usePlants() {
  return useQuery({
    queryKey: ["catalog", "plants"],
    queryFn: ({ signal }) => api<Plant[]>("/api/catalog/plants", { signal }),
    staleTime: 30 * 60_000,
  });
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
