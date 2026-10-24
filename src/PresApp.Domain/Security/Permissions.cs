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
    public const string GroupsManage = "groups.manage";
    public const string CategoriesManage = "categories.manage";
    public const string SimproSync = "simpro.sync";
    public const string CatalogManage = "catalog.manage";
    public const string UsersManage = "users.manage";

    public static readonly IReadOnlyList<PermissionInfo> All =
    [
        new(AssetsView, "Ver registros, dashboards e historial"),
        new(ExcelExport, "Descargar Excel de una unidad"),
        new(ReadingsWrite, "Registrar lecturas de monitoreo"),
        new(GatesEdit, "Editar estados de gates y comentarios"),
        new(AssetsEdit, "Editar el resto de campos y la documentación por gate"),
        new(AssetsMarkPreserved, "Marcar un activo como preservado"),
        new(BulkEdit, "Edición masiva"),
        new(AssetsManage, "Crear, desactivar o reactivar activos"),
        new(ExcelImport, "Cargar Excel y resolver incidencias de carga"),
        new(GroupsManage, "Workpacks"),
        new(CategoriesManage, "Categorías del dashboard"),
        new(SimproSync, "Sincronizar con Simpro"),
        new(CatalogManage, "Catálogos y campos dinámicos"),
        new(UsersManage, "Usuarios y roles"),
    ];

    public static bool IsKnown(string permission) => All.Any(p => p.Key == permission);
}

public sealed record PermissionInfo(string Key, string Description);
