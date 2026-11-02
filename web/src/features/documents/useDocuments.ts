import { useMutation, useQuery, useQueryClient, type QueryClient } from "@tanstack/react-query";
import { api, postForm } from "../../api/client";
import type { StoredDocument } from "../../api/types";

export const MAX_FILE_MB = 25;

export const documentUrl = (id: string, download = false) => `/api/documents/${id}/file${download ? "?download=1" : ""}`;

export function useAssetDocuments(assetId: string, enabled = true) {
  return useQuery({
    queryKey: ["documents", assetId],
    queryFn: ({ signal }) => api<StoredDocument[]>(`/api/assets/${assetId}/documents`, { signal }),
    enabled,
  });
}

/** A document can touch several assets: refresh every asset view, their documents and the registers. */
function refresh(client: QueryClient) {
  client.invalidateQueries({ queryKey: ["documents"] });
  client.invalidateQueries({ queryKey: ["asset"] });
  client.invalidateQueries({ queryKey: ["register"] });
  client.invalidateQueries({ queryKey: ["unit-summary"] });
}

export interface UploadInput {
  file: File;
  note: string | null;
  links: { assetId: string; gate: number | null }[];
}

export function useUploadDocument() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: ({ file, note, links }: UploadInput) => {
      const form = new FormData();
      form.append("file", file);
      form.append("links", JSON.stringify(links));
      if (note) form.append("note", note);
      return postForm<StoredDocument>("/api/documents", form);
    },
    onSuccess: () => refresh(client),
  });
}

export function useUnlinkDocument() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: ({ documentId, linkId }: { documentId: string; linkId: number }) =>
      api<void>(`/api/documents/${documentId}/links/${linkId}`, { method: "DELETE" }),
    onSuccess: () => refresh(client),
  });
}

export function useDeleteDocument() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (documentId: string) => api<void>(`/api/documents/${documentId}`, { method: "DELETE" }),
    onSuccess: () => refresh(client),
  });
}

export function formatSize(bytes: number) {
  if (bytes < 1024 * 1024) return `${Math.max(1, Math.round(bytes / 1024))} KB`;
  return `${(bytes / 1024 / 1024).toFixed(1)} MB`;
}
