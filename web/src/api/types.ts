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
  excelPage: "excel.page",
  historySource: "history.source",
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

export type AssetType = "Equipment" | "Pipeline" | "ElectricalInstrument";

export interface FieldDef {
  key: string;
  label: string;
  section: string;
  kind: "Text" | "LongText" | "Choice" | "Date" | "Number" | "ReadOnly";
  optionSet: string | null;
  maxLength: number;
  permission: string;
  gate: number | null;
  bulkEditable: boolean;
  onlyFor: AssetType | null;
}

export interface OptionSet {
  key: string;
  label: string;
  values: string[];
}

export interface Discipline {
  id: number;
  name: string;
  assetType: AssetType;
  prefix: string | null;
}

export interface Measurement {
  id: number;
  key: string;
  label: string;
  unitOfMeasure: string | null;
  gate: number | null;
  evidenceForGate: number | null;
  minValue: number | null;
  maxValue: number | null;
}

export interface RegisterRow {
  id: string;
  code: string;
  tag: string | null;
  description: string | null;
  discipline: string;
  priority: string | null;
  currentGate: string;
  gates: (string | null)[];
  assetAvailability: string | null;
  preservationStatus: string | null;
  thirdPartyInspection: string | null;
  emWalkdown: string | null;
  preservedByEu: string | null;
  actionBy: string | null;
  n2BlanketStatus: string | null;
  currentActivity: string | null;
  preservationRemarks: string | null;
  lastFieldActionDate: string | null;
  preservedAtUtc: string | null;
  documentsPending: number;
  isActive: boolean;
  updatedAtUtc: string;
  line: LineInfo | null;
  groups: string[] | null;
  circuit: string | null;
  custom: Record<string, string | null> | null;
}

export type CustomFieldType = "Text" | "Number" | "Date" | "List" | "YesNo";

export interface CustomField {
  id: number;
  assetType: AssetType;
  key: string;
  label: string;
  dataType: CustomFieldType;
  options: string[];
  section: string | null;
  sortOrder: number;
  showInRegister: boolean;
  isActive: boolean;
  values: number;
}

export interface CustomValue {
  key: string;
  label: string;
  dataType: CustomFieldType;
  options: string[];
  section: string | null;
  value: string | null;
}

export interface LineInfo {
  sizeIn: number | null;
  pipeSpec: string | null;
  pressureClass: string | null;
  flangeType: string | null;
  gasket: string | null;
  service: string | null;
  pidNumber: string | null;
  lengthM: number | null;
}

export interface PipeSpec {
  id: number;
  code: string;
  pressureClass: string | null;
  flangeType: string | null;
  gasket: string | null;
  description: string | null;
  isActive: boolean;
  lines: number;
}

export interface PipeSpecImportRow {
  row: number;
  code: string;
  pressureClass: string | null;
  flangeType: string | null;
  gasket: string | null;
  description: string | null;
  outcome: "New" | "Added" | "Update" | "Updated" | "Unchanged" | "Invalid";
  reason: string | null;
  before: string | null;
}

export interface PipeSpecImportResult {
  sheet: string | null;
  columns: string[];
  new: number;
  updated: number;
  unchanged: number;
  invalid: number;
  rows: PipeSpecImportRow[];
}

export interface BulkLineRow {
  row: number;
  lineNumber: string;
  sizeIn: number | null;
  pipeSpec: string | null;
  pressureClass: string | null;
  flangeType: string | null;
  service: string | null;
  lengthM: number | null;
  outcome: "New" | "Created" | "Exists" | "Invalid" | "Possible match";
  reason: string | null;
  assetId: string | null;
  code: string | null;
  warning: string | null;
}

export type LineCodeKind = "Service";

export interface LineCode {
  id: number;
  kind: LineCodeKind;
  code: string;
  description: string;
  isActive: boolean;
}

export interface BulkLinesResult {
  toCreate: number;
  existing: number;
  invalid: number;
  rows: BulkLineRow[];
}

export interface GateDoc {
  gate: number;
  status: "Pending" | "Received" | "NotApplicable";
  receivedOn: string | null;
  reference: string | null;
  note: string | null;
  updatedBy: string;
  updatedAtUtc: string;
}

export interface AssetProfile {
  id: string;
  code: string;
  type: AssetType;
  discipline: { id: number; name: string };
  unit: { id: number; name: string; number: number; plant: string };
  isActive: boolean;
  simproFlangeId: string | null;
  currentGate: string;
  suggestedCurrentGate: string;
  values: Record<string, string | null>;
  gateDocuments: GateDoc[];
  groups: { id: number; code: string; name: string; type: string }[];
  preservedAtUtc: string | null;
  preservedBy: string | null;
  updatedBy: string;
  updatedAtUtc: string;
}

export interface Proposals {
  currentGate: string | null;
  lastFieldActionDate: string | null;
}

export interface Reading {
  id: number;
  measurement: string;
  label: string;
  value: number;
  unitOfMeasure: string | null;
  measuredAtUtc: string;
  recordedAtUtc: string;
  recordedBy: string;
  source: string;
  phase: "During" | "Post";
  note: string | null;
}

export interface HistoryEntry {
  atUtc: string;
  assetId: string | null;
  assetCode: string | null;
  assetTag: string | null;
  field: string;
  label: string;
  oldValue: string | null;
  newValue: string | null;
  by: string;
  source: string | null;
}

export interface SearchHit {
  id: string;
  code: string;
  tag: string | null;
  description: string | null;
  unit: string;
  type: AssetType;
  isActive: boolean;
}

export interface BulkRow {
  assetId: string;
  code: string;
  tag: string | null;
  current: string | null;
  new: string | null;
  outcome: "Change" | "No change" | "Skipped";
  reason: string | null;
}

export interface BulkResult {
  changed: number;
  unchanged: number;
  skipped: number;
  rows: BulkRow[];
}

export interface UnitSummary {
  equipment: number;
  electricalInstrument: number;
  pipelines: number;
  inactive: number;
  equipmentByCurrentGate: Record<string, number>;
  documentsPending: number;
}

// ---------------------------------------------------------------- Excel, imports, dashboard

export interface ImportCounts {
  applied: number;
  derived: number;
  noChange: number;
  invalid: number;
  conflict: number;
  rejected: number;
  warning: number;
  created: number;
  issues: number;
}

export type ImportStatus = "Previewed" | "Applied" | "AppliedWithIssues" | "Rejected" | "Failed" | "Cancelled";
export type ImportOutcome = "Applied" | "Derived" | "No change" | "Invalid" | "Conflict" | "Rejected" | "Warning" | "Created";

export interface ImportRun {
  id: number;
  kind: "Update" | "Onboarding";
  fileName: string;
  unitId: number | null;
  unitName: string | null;
  sessionId: string | null;
  uploadedBy: string;
  uploadedAtUtc: string;
  appliedAtUtc: string | null;
  status: ImportStatus;
  message: string | null;
  rowsInFile: number;
  counts: ImportCounts;
  exportedAtUtc: string | null;
  exportedBy: string | null;
}

export interface ImportChange {
  id: number;
  runId: number;
  assetId: string | null;
  assetCode: string | null;
  assetTag: string | null;
  rowNumber: number | null;
  field: string;
  fieldLabel: string;
  baseValue: string | null;
  excelValue: string | null;
  currentValue: string | null;
  newValue: string | null;
  outcome: ImportOutcome;
  reason: string | null;
  resolution: "None" | "Open" | "Resolved";
  resolvedBy: string | null;
  resolvedAtUtc: string | null;
  resolutionNote: string | null;
}

export interface ImportRunDetail {
  run: ImportRun;
  changes: ImportChange[];
}

export interface ExcelSession {
  id: string;
  fileName: string;
  exportedAtUtc: string;
  exportedBy: string;
  status: "Open" | "Processing" | "Processed";
  rowCount: number;
  processedAtUtc: string | null;
  /** Exported by the old Power App flow: kept for the record, cannot be uploaded here. */
  legacy: boolean;
}

export interface UnitExcelOverview {
  hasTemplate: boolean;
  templateFileName: string | null;
  templateUploadedAtUtc: string | null;
  maxAgeHours: number;
  sessions: ExcelSession[];
  runs: ImportRun[];
}

export interface Issue {
  change: ImportChange;
  fileName: string;
  unitId: number | null;
  unitName: string | null;
  uploadedAtUtc: string;
  uploadedBy: string;
}

export interface ExcelTemplateInfo {
  unitId: number;
  unitName: string;
  fileName: string | null;
  uploadedAtUtc: string | null;
  uploadedBy: string | null;
  categories: number;
}

export interface ActivityRow {
  label: string;
  completed: number;
  pending: number;
  hold: number;
  other: number;
  total: number;
  completionPct: number;
}

export interface DisciplineRow {
  discipline: string;
  total: number;
  walkdown: number;
  g1: number;
  g2: number;
  g3: number;
  g4: number;
  euPreserved: number;
  progressPct: number;
}

export interface CategoryRow {
  id: number;
  label: string;
  section: "Hold" | "NotApplicable";
  priority: number;
  keywords: string[];
  actionBy: string | null;
  emailAction: string | null;
  note: string | null;
  count: number;
  assets: { id: string; code: string; tag: string | null }[];
}

export interface Dashboard {
  unitId: number;
  unitName: string;
  plantName: string;
  totalTags: number;
  activities: ActivityRow[];
  disciplines: DisciplineRow[];
  hold: CategoryRow[];
  totalHold: number;
  notApplicable: CategoryRow[];
  totalNotApplicable: number;
  grandTotal: number;
  availableForPreservation: number;
  preserved: number;
  readyForPreservation: number;
  byCurrentGate: Record<string, number>;
  documentsPending: number;
  electricalInstrumentCount: number;
  lineCount: number;
  lineLengthM: number;
  linesBlanketed: number;
  inactiveCount: number;
}

// ---------------------------------------------------------------- Simpro

export interface SimproRun {
  id: number;
  mode: "Preview" | "Apply" | "Scheduled" | "Undo";
  status: "Running" | "Previewed" | "Applied" | "HeldForConflicts" | "Failed" | "Undone";
  triggeredBy: string;
  startedAtUtc: string;
  finishedAtUtc: string | null;
  createdCount: number;
  updatedCount: number;
  fieldCount: number;
  heldCount: number;
  rejectedCount: number;
  notInAppCount: number;
  message: string | null;
  undoOfRunId: number | null;
  canUndo: boolean;
}

export interface SimproItem {
  id: number;
  assetId: string;
  assetCode: string;
  assetTag: string | null;
  unitName: string | null;
  field: string;
  fieldLabel: string;
  simproValue: string | null;
  newValue: string | null;
  lastSyncedValue: string | null;
  outcome: string;
  reason: string | null;
  canCopy: boolean;
  forced: boolean;
}

export interface SimproRunDetail {
  run: SimproRun;
  items: SimproItem[];
}

export interface SimproSettings {
  configured: boolean;
  scheduleEnabled: boolean;
  scheduleTime: string;
  syncUsername: string;
  forcedCount: number;
}

// ---------------------------------------------------------------- monitoring

export type MonitoringStatus = "Overdue" | "No reading since preserved" | "OK" | "During preservation" | "Removed from site";

export interface MonitoringRow {
  id: string;
  code: string;
  tag: string | null;
  description: string | null;
  type: AssetType;
  discipline: string;
  currentGate: string;
  phase: "During" | "Post";
  preservedAtUtc: string | null;
  n2Barg: number | null;
  n2AtUtc: string | null;
  daysSinceN2: number | null;
  pressureBarg: number | null;
  o2Pct: number | null;
  dewPointC: number | null;
  lastReadingAtUtc: string | null;
  status: MonitoringStatus;
  lowN2: boolean;
  groups: string[];
}

export interface MonitoringSettings {
  intervalDays: number;
  minN2Barg: number | null;
}

export interface MonitoringView {
  settings: MonitoringSettings;
  summary: { post: number; during: number; overdue: number; noReading: number; lowN2: number; ok: number };
  rows: MonitoringRow[];
}

export interface RoundResult {
  assets: number;
  readings: number;
  proposals: { assetId: string; code: string; tag: string | null; currentGate: string | null; lastFieldActionDate: string | null }[];
}

// ---------------------------------------------------------------- groups

export interface GroupType {
  id: number;
  name: string;
  prefix: string;
}

export interface GroupProgress {
  members: number;
  equipment: number;
  lines: number;
  electricalInstrument: number;
  gatesCompleted: number[];
  preserved: number;
  removedFromSite: number;
}

export interface Group {
  id: number;
  code: string;
  name: string;
  description: string | null;
  typeId: number;
  type: string;
  unitId: number | null;
  unitName: string | null;
  isActive: boolean;
  progress: GroupProgress;
  updatedBy: string;
  updatedAtUtc: string;
}

export interface GroupMember {
  id: string;
  code: string;
  tag: string | null;
  description: string | null;
  type: AssetType;
  discipline: string;
  currentGate: string;
  gates: (string | null)[];
  preservedAtUtc: string | null;
  isActive: boolean;
  addedAtUtc: string;
  addedBy: string;
}

export interface GroupDetail {
  group: Group;
  members: GroupMember[];
}

export interface MembersResult {
  added: number;
  alreadyIn: number;
  removed: number;
  detail: GroupDetail;
}

