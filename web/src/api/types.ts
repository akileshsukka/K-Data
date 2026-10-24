export interface Me {
  email: string;
  displayName: string;
  role: string;
  permissions: string[];
  authMethod: string;
}

export interface Unit {
  id: number;
  name: string;
  number: number;
}

export interface Plant {
  id: number;
  name: string;
  units: Unit[];
}

export interface CloudflareResult {
  configured: boolean;
  success: boolean;
  message: string;
}

export interface AppUser {
  id: number;
  email: string;
  displayName: string;
  roleId: number;
  role: string;
  isActive: boolean;
  createdAtUtc: string;
  lastLoginAtUtc: string | null;
  hasEmergencyPassword: boolean;
}

export interface Role {
  id: number;
  name: string;
  description: string | null;
  isSystem: boolean;
  permissions: string[];
  userCount: number;
}

export interface PermissionInfo {
  key: string;
  description: string;
}

export interface LoginRecord {
  atUtc: string;
  email: string;
  success: boolean;
  method: string;
  ipAddress: string | null;
  detail: string | null;
}

/** Permission keys checked by the API (must match PresApp.Domain.Security.Permissions). */
export const Perm = {
  assetsView: "assets.view",
  excelExport: "excel.export",
  readingsWrite: "readings.write",
  gatesEdit: "gates.edit",
  assetsEdit: "assets.edit",
  assetsMarkPreserved: "assets.markPreserved",
  bulkEdit: "bulk.edit",
  assetsManage: "assets.manage",
  excelImport: "excel.import",
  groupsManage: "groups.manage",
  categoriesManage: "categories.manage",
  simproSync: "simpro.sync",
  catalogManage: "catalog.manage",
  usersManage: "users.manage",
} as const;
