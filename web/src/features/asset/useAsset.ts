import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api } from "../../api/client";
import type { AssetProfile, HistoryEntry, Proposals, Reading } from "../../api/types";

export interface FieldChange {
  field: string;
  from: string | null;
  to: string | null;
}

export const assetKey = (id: string) => ["asset", id] as const;

export function useAsset(id: string) {
  return useQuery({ queryKey: assetKey(id), queryFn: ({ signal }) => api<AssetProfile>(`/api/assets/${id}`, { signal }) });
}

export function useAssetReadings(id: string) {
  return useQuery({ queryKey: ["asset", id, "readings"], queryFn: ({ signal }) => api<Reading[]>(`/api/assets/${id}/readings`, { signal }) });
}

export function useAssetHistory(id: string) {
  return useQuery({ queryKey: ["asset", id, "history"], queryFn: ({ signal }) => api<HistoryEntry[]>(`/api/assets/${id}/history`, { signal }) });
}

/** Any change to an asset refreshes its profile, history and the registers that list it. */
function useAssetMutation<TBody, TResult>(id: string, run: (body: TBody) => Promise<TResult>, pick: (r: TResult) => AssetProfile) {
  const client = useQueryClient();
  return useMutation({
    mutationFn: run,
    onSuccess: (result) => {
      client.setQueryData(assetKey(id), pick(result));
      client.invalidateQueries({ queryKey: ["asset", id, "history"] });
      client.invalidateQueries({ queryKey: ["asset", id, "readings"] });
      client.invalidateQueries({ queryKey: ["register"] });
      client.invalidateQueries({ queryKey: ["unit-summary"] });
    },
  });
}

export function usePatchAsset(id: string) {
  return useAssetMutation(
    id,
    (changes: FieldChange[]) => api<{ asset: AssetProfile; proposals: Proposals }>(`/api/assets/${id}`, { method: "PATCH", body: { changes } }),
    (r) => r.asset,
  );
}

export function useSetCurrentGate(id: string) {
  return useAssetMutation(
    id,
    (body: { value: string; expected: string }) => api<AssetProfile>(`/api/assets/${id}/current-gate`, { method: "POST", body }),
    (r) => r,
  );
}

export function useMarkPreserved(id: string) {
  return useAssetMutation(
    id,
    (preservedOn: string | null) => api<AssetProfile>(`/api/assets/${id}/preserved`, { method: "POST", body: { preservedOn } }),
    (r) => r,
  );
}

export function useSetGateDoc(id: string) {
  return useAssetMutation(
    id,
    ({ gate, ...body }: { gate: number; status: string; receivedOn: string | null; reference: string | null; note: string | null }) =>
      api<AssetProfile>(`/api/assets/${id}/gates/${gate}/documentation`, { method: "PUT", body }),
    (r) => r,
  );
}

export function useAddReadings(id: string) {
  return useAssetMutation(
    id,
    (body: { measuredAtUtc: string; note: string | null; values: { measurementTypeId: number; value: number }[] }) =>
      api<{ saved: number; proposals: Proposals; asset: AssetProfile }>(`/api/assets/${id}/readings`, { method: "POST", body }),
    (r) => r.asset,
  );
}

export function useSetActive(id: string) {
  return useAssetMutation(id, (active: boolean) => api<AssetProfile>(`/api/assets/${id}/active?active=${active}`, { method: "POST" }), (r) => r);
}

/** Hold / N/A dashboard category of the asset. Expected = the category id the user saw (conflict check). */
export function useSetCategory(id: string) {
  return useAssetMutation(
    id,
    (body: { categoryId: number | null; expected: number | null }) =>
      api<AssetProfile>(`/api/assets/${id}/dashboard-category`, { method: "PUT", body }),
    (r) => r,
  );
}
