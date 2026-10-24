import { createBrowserRouter, Navigate } from "react-router";
import { AppShell } from "./AppShell";
import { lastUnit } from "./useCatalog";
import { UnitPickerPage } from "../features/units/UnitPickerPage";
import { UnitHomePage } from "../features/units/UnitHomePage";
import { UsersPage } from "../features/admin/UsersPage";
import { RolesPage } from "../features/admin/RolesPage";
import { LoginsPage } from "../features/admin/LoginsPage";
import { RequirePermission } from "./RequirePermission";
import { Perm } from "../api/types";

function StartRedirect() {
  const id = lastUnit.get();
  return <Navigate to={id ? `/u/${id}` : "/unidades"} replace />;
}

export const router = createBrowserRouter([
  {
    element: <AppShell />,
    children: [
      { index: true, element: <StartRedirect /> },
      { path: "unidades", element: <UnitPickerPage /> },
      { path: "u/:unitId", element: <UnitHomePage /> },
      {
        path: "admin",
        element: <RequirePermission permission={Perm.usersManage} />,
        children: [
          { index: true, element: <Navigate to="usuarios" replace /> },
          { path: "usuarios", element: <UsersPage /> },
          { path: "roles", element: <RolesPage /> },
          { path: "ingresos", element: <LoginsPage /> },
        ],
      },
      { path: "*", element: <Navigate to="/" replace /> },
    ],
  },
]);
