# PresApp

Aplicación web de preservación de EnerMech: registro de equipos, líneas y E&I, gates, monitoreo, workpacks, Excel por unidad y sincronización con Simpro. Todo corre en el servidor local (EM-L-5236) y los usuarios entran por Cloudflare.

Arquitectura y plan completo: [PresApp — Arquitectura y plan de construcción](https://claude.ai/artifact/1NYGP7v3arj4iH6BG2cUFX).

## Estructura

```
PresApp/
├─ src/
│  ├─ PresApp.Domain/          Entidades y reglas de negocio, sin dependencias
│  ├─ PresApp.Infrastructure/  Base de datos (EF Core + SQL Server), auditoría, datos semilla, migraciones
│  └─ PresApp.Api/             API REST, login con Cloudflare, permisos; sirve también el front (wwwroot)
├─ web/                        Front: React + TypeScript + Vite + Tailwind
├─ tests/PresApp.Tests/        Pruebas automáticas (API real sobre SQLite en memoria)
└─ deploy/                     Publicación, servicio de Windows, SQL (base y backups), Cloudflare Tunnel
```

## Requisitos

| Dónde | Qué |
| --- | --- |
| Desarrollo (EM-L-4942) | .NET 10 SDK, Node.js 24 LTS, Git, SQL Server LocalDB (viene con Visual Studio) o SQL Server 2022 Developer |
| Servidor (EM-L-5236) | ASP.NET Core 10 Runtime (Hosting Bundle), SQL Server 2022 Standard (ya instalado), `cloudflared` |

## Desarrollo

```powershell
# 1. API (crea la base PresApp_Dev en LocalDB y entra como diego.perez@enermech.com, sin Cloudflare)
cd src\PresApp.Api
dotnet run

# 2. Front, en otra terminal (http://localhost:5173, con recarga en vivo)
cd web
npm install
npm run dev
```

- El usuario de desarrollo se define en `src/PresApp.Api/appsettings.Development.json` (`Auth:DevelopmentUserEmail`). Ese modo solo existe en el entorno Development.
- Otra base de datos: crea `src/PresApp.Api/appsettings.Local.json` (no se sube a Git) con tu `ConnectionStrings:PresApp`.
- Pruebas: `dotnet test`.
- Cambios al modelo de datos: `dotnet ef migrations add <Nombre> -p src/PresApp.Infrastructure -s src/PresApp.Infrastructure -o Data/Migrations`. PresApp aplica las migraciones sola al arrancar.

## Instalación en el servidor (una sola vez)

1. **Base de datos.** En SSMS, en EM-L-5236, ejecuta `deploy/sql/01-create-database.sql`. Crea la base `PresApp` vacía y el login del servicio. Las tablas las crea PresApp al arrancar.
2. **Backups.** Ajusta las dos carpetas al inicio de `deploy/sql/02-backup-jobs.sql` y ejecútalo. Crea el job de SQL Agent: completo diario a las 02:00, diferencial cada 4 horas, 30 días de retención y copia a un segundo lugar.
3. **Publicar.** En el equipo de desarrollo: `.\deploy\publish.ps1`. Copia la carpeta `publish` al servidor.
4. **Servicio.** En el servidor, como administrador: `.\deploy\install-service.ps1`. Instala el servicio `PresApp` (arranque automático, reinicio si falla) y crea `C:\PresApp\app\appsettings.Production.json`.
5. **Cloudflare** (siguiente sección). Completa `Auth` y `Cloudflare` en `appsettings.Production.json` y reinicia el servicio.
6. **Acceso de emergencia.** En el servidor: `C:\PresApp\app\PresApp.Api.exe set-emergency-password diego.perez@enermech.com`. Se usa abriendo `http://127.0.0.1:5081` en el propio servidor cuando fallan internet o Cloudflare.

Actualizar a una versión nueva: repetir los pasos 3 y 4. El script hace un backup de la base antes de copiar.

## Cloudflare

Los nombres de menús pueden variar en el panel de Cloudflare Zero Trust.

1. **Túnel:** en Zero Trust → Networks → Tunnels, crea un túnel `presapp` e instala el conector en el servidor con el comando que da el panel (queda como servicio de Windows). Agrega el hostname público `presapp.<dominio>` → `http://127.0.0.1:5080`. Nunca publiques el puerto 5081. Ejemplo de archivo: `deploy/cloudflared/config.example.yml`.
2. **Login por código:** en Settings → Authentication, activa *One-time PIN*.
3. **Grupo de usuarios:** en Access → Access groups, crea `PresApp users`. PresApp mantiene su lista de correos.
4. **Aplicación:** en Access → Applications, agrega una aplicación *Self-hosted* para `presapp.<dominio>`, con una sesión de 1 mes y una política *Allow* que incluya el grupo `PresApp users`. Copia el **Application Audience (AUD) tag**.
5. **Configurar PresApp** en `appsettings.Production.json`:
   - `Auth:CloudflareTeamDomain` = `<equipo>.cloudflareaccess.com`
   - `Auth:CloudflareAudience` = el AUD tag
   - `Cloudflare:AccountId`, `Cloudflare:AccessGroupId` y `Cloudflare:ApiToken` (token con permiso *Access: Organizations, Identity Providers, and Groups — Edit*). Con esto, al crear o desactivar un usuario en PresApp se actualiza la lista de Cloudflare y se revocan sus sesiones. Sin ellos, la lista se mantiene a mano en Cloudflare.

## Seguridad en breve

- Un solo login: código al correo (Cloudflare). PresApp valida la firma del token y busca el correo entre sus usuarios activos.
- Roles: Consulta, Técnico de campo, Ingeniero, Admin. Cada endpoint exige su permiso en el servidor.
- Cada cambio de campo queda en `ChangeLog` (quién, cuándo, antes, después, origen), en la misma transacción que el cambio.
- Concurrencia optimista (`rowversion`): nadie sobrescribe sin saberlo el cambio de otra persona.
