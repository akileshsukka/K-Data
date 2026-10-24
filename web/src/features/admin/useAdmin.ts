import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api } from "../../api/client";
import type { AppUser, CloudflareResult, LoginRecord, PermissionInfo, Role } from "../../api/types";

export interface SaveUser {
  email: string;
  displayName: string;
  roleId: number;
}

interface UserChange {
  user: AppUser;
  cloudflare: CloudflareResult;
}

export const useUsers = () =>
  useQuery({ queryKey: ["admin", "users"], queryFn: ({ signal }) => api<AppUser[]>("/api/admin/users", { signal }) });

export const useRoles = () =>
  useQuery({ queryKey: ["admin", "roles"], queryFn: ({ signal }) => api<Role[]>("/api/admin/roles", { signal }) });

export const usePermissionCatalog = () =>
  useQuery({
    queryKey: ["admin", "permissions"],
    queryFn: ({ signal }) => api<PermissionInfo[]>("/api/admin/permissions", { signal }),
    staleTime: Infinity,
  });

export const useLogins = () =>
  useQuery({ queryKey: ["admin", "logins"], queryFn: ({ signal }) => api<LoginRecord[]>("/api/admin/logins?take=300", { signal }) });

export function useSaveUser() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: ({ id, data }: { id?: number; data: SaveUser }) =>
      id
        ? api<UserChange>(`/api/admin/users/${id}`, { method: "PUT", body: { ...data } })
        : api<UserChange>("/api/admin/users", { method: "POST", body: { ...data } }),
    onSuccess: () => client.invalidateQueries({ queryKey: ["admin"] }),
  });
}

export function useSetUserActive() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: ({ id, active }: { id: number; active: boolean }) =>
      api<UserChange>(`/api/admin/users/${id}/active?active=${active}`, { method: "POST" }),
    onSuccess: () => client.invalidateQueries({ queryKey: ["admin"] }),
  });
}

export function useSaveRolePermissions() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: ({ id, permissions }: { id: number; permissions: string[] }) =>
      api<void>(`/api/admin/roles/${id}/permissions`, { method: "PUT", body: { permissions } }),
    onSuccess: () => client.invalidateQueries({ queryKey: ["admin", "roles"] }),
  });
}
