namespace PresApp.Domain.Security;

/// <summary>
/// Every permission the API checks. Roles map to these in the database, so an admin can change
/// what a role can do without code changes.
/// </summary>
public static class Permissions
{
    public const string AssetsView = "assets.view";
    public const string ExcelExport = "excel.export";
    public const string ReadingsWrite = "readings.write";
    public const string GatesEdit = "gates.edit";
    public const string AssetsEdit = "assets.edit";
    public const string AssetsMarkPreserved = "assets.markPreserved";
    public const string BulkEdit = "bulk.edit";
    public const string AssetsManage = "assets.manage";
    public const string ExcelImport = "excel.import";
    public const string ExcelPage = "excel.page";
    public const string HistorySource = "history.source";
    public const string GroupsManage = "groups.manage";
    public const string CategoriesManage = "categories.manage";
    public const string SimproSync = "simpro.sync";
    public const string CatalogManage = "catalog.manage";
    public const string UsersManage = "users.manage";

    public static readonly IReadOnlyList<PermissionInfo> All =
    [
        new(AssetsView, "View registers, dashboards (piping cards included), history and export the Unit overview PDF"),
        new(ExcelExport, "Download a unit Excel"),
        new(ReadingsWrite, "Record monitoring readings"),
        new(GatesEdit, "Edit gate statuses and comments"),
        new(AssetsEdit, "Edit all other fields and gate documentation"),
        new(AssetsMarkPreserved, "Mark an asset as preserved"),
        new(BulkEdit, "Bulk edit"),
        new(AssetsManage, "Create, deactivate or reactivate assets"),
        new(ExcelImport, "Upload Excel files and resolve import issues"),
        new(ExcelPage, "See the Excel page (downloads and uploads of the unit file)"),
        new(HistorySource, "See where each change came from in the history (App, Excel, Bulk edit, Simpro…)"),
        new(GroupsManage, "Manage workpacks"),
        new(CategoriesManage, "Manage dashboard categories"),
        new(SimproSync, "Sync with Simpro"),
        new(CatalogManage, "Manage lists and custom fields"),
        new(UsersManage, "Manage users and roles"),
    ];

    public static bool IsKnown(string permission) => All.Any(p => p.Key == permission);
}

public sealed record PermissionInfo(string Key, string Description);
