import { createBrowserRouter, Navigate } from "react-router";
import { AppShell } from "./AppShell";
import { lastUnit } from "./useCatalog";
import { RequirePermission } from "./RequirePermission";
import { Perm } from "../api/types";
import { UnitPickerPage } from "../features/units/UnitPickerPage";
import { UnitHomePage } from "../features/units/UnitHomePage";
import { RegisterPage } from "../features/register/RegisterPage";
import { AssetPage } from "../features/asset/AssetPage";
import { MonitoringPage } from "../features/monitoring/MonitoringPage";
import { GroupsPage } from "../features/groups/GroupsPage";
import { GroupPage } from "../features/groups/GroupPage";
import { ListsPage } from "../features/admin/ListsPage";
import { CustomFieldsPage } from "../features/admin/CustomFieldsPage";
import { PipeSpecsPage } from "../features/admin/PipeSpecsPage";
import { LineCodesPage } from "../features/admin/LineCodesPage";
import { PasteLinesPage } from "../features/lines/PasteLinesPage";
import { NewAssetPage } from "../features/asset/NewAssetPage";
import { BulkEditPage } from "../features/bulk/BulkEditPage";
import { UnitHistoryPage } from "../features/history/UnitHistoryPage";
import { UsersPage } from "../features/admin/UsersPage";
import { RolesPage } from "../features/admin/RolesPage";
import { LoginsPage } from "../features/admin/LoginsPage";
import { ImportIssuesPage } from "../features/admin/ImportIssuesPage";
import { ExcelTemplatesPage } from "../features/admin/ExcelTemplatesPage";
import { ExcelPage } from "../features/excel/ExcelPage";
import { ImportPage } from "../features/excel/ImportPage";
import { CategoriesPage } from "../features/units/CategoriesPage";
import { SimproPage } from "../features/simpro/SimproPage";
import { SimproRunPage } from "../features/simpro/SimproRunPage";

function StartRedirect() {
  const id = lastUnit.get();
  return <Navigate to={id ? `/u/${id}` : "/units"} replace />;
}

export const router = createBrowserRouter([
  {
    element: <AppShell />,
    children: [
      { index: true, element: <StartRedirect /> },
      { path: "units", element: <UnitPickerPage /> },
      { path: "u/:unitId", element: <UnitHomePage /> },
      { path: "u/:unitId/equipment", element: <RegisterPage key="eq" type="Equipment" /> },
      { path: "u/:unitId/lines", element: <RegisterPage key="pl" type="Pipeline" /> },
      { path: "u/:unitId/ei", element: <RegisterPage key="ei" type="ElectricalInstrument" /> },
      { path: "u/:unitId/history", element: <UnitHistoryPage /> },
      { path: "u/:unitId/monitoring", element: <MonitoringPage /> },
      { path: "u/:unitId/groups", element: <GroupsPage /> },
      { path: "groups/:id", element: <GroupPage /> },
      {
        element: <RequirePermission permission={Perm.bulkEdit} />,
        children: [{ path: "u/:unitId/bulk", element: <BulkEditPage /> }],
      },
      {
        element: <RequirePermission permission={Perm.assetsManage} />,
        children: [
          { path: "u/:unitId/new", element: <NewAssetPage /> },
          { path: "u/:unitId/lines/paste", element: <PasteLinesPage /> },
        ],
      },
      {
        element: <RequirePermission permission={Perm.excelPage} />,
        children: [{ path: "u/:unitId/excel", element: <ExcelPage /> }],
      },
      {
        element: <RequirePermission permission={Perm.categoriesManage} />,
        children: [{ path: "u/:unitId/categories", element: <CategoriesPage /> }],
      },
      { path: "assets/:id", element: <AssetPage /> },
      {
        element: <RequirePermission permission={Perm.excelImport} />,
        children: [
          { path: "imports/:id", element: <ImportPage /> },
          { path: "admin/import-issues", element: <ImportIssuesPage /> },
        ],
      },
      {
        element: <RequirePermission permission={Perm.catalogManage} />,
        children: [
          { path: "admin/excel-templates", element: <ExcelTemplatesPage /> },
          { path: "admin/line-codes", element: <LineCodesPage /> },
          { path: "admin/pipe-specs", element: <PipeSpecsPage /> },
          { path: "admin/custom-fields", element: <CustomFieldsPage /> },
          { path: "admin/lists", element: <ListsPage /> },
        ],
      },
      {
        element: <RequirePermission permission={Perm.simproSync} />,
        children: [
          { path: "admin/simpro", element: <SimproPage /> },
          { path: "admin/simpro/runs/:id", element: <SimproRunPage /> },
        ],
      },
      {
        path: "admin",
        element: <RequirePermission permission={Perm.usersManage} />,
        children: [
          { index: true, element: <Navigate to="users" replace /> },
          { path: "users", element: <UsersPage /> },
          { path: "roles", element: <RolesPage /> },
          { path: "sign-ins", element: <LoginsPage /> },
        ],
      },
      { path: "*", element: <Navigate to="/" replace /> },
    ],
  },
]);
