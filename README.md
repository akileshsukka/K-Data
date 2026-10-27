# PresApp

EnerMech preservation web application: equipment, pipeline and E&I registers, gates, monitoring, workpacks, per-unit Excel and Simpro sync. Everything runs on the local server (EM-L-5236); users sign in through Cloudflare.

Architecture and full plan: [PresApp — Architecture and build plan](https://claude.ai/artifact/1NYGP7v3arj4iH6BG2cUFX).

## Structure

```
PresApp/
├─ src/
│  ├─ PresApp.Domain/          Entities and business rules, no dependencies
│  ├─ PresApp.Infrastructure/  Database (EF Core + SQL Server), auditing, seed data, migrations
│  └─ PresApp.Api/             REST API, Cloudflare sign-in, permissions; also serves the front end (wwwroot)
├─ web/                        Front end: React + TypeScript + Vite + Tailwind
├─ tests/PresApp.Tests/        Automated tests (the real API on an in-memory SQLite database)
└─ deploy/                     Publishing, Windows service, SQL (database and backups), Cloudflare Tunnel
```

## Requirements

| Where | What |
| --- | --- |
| Development (EM-L-4942) | .NET 10 SDK, Node.js 24 LTS, Git, SQL Server LocalDB (ships with Visual Studio) or SQL Server 2022 Developer |
| Server (EM-L-5236) | ASP.NET Core 10 Runtime (Hosting Bundle), SQL Server 2022 Standard (already installed), `cloudflared` |

## Development

```powershell
# 1. API: creates the PresApp_Dev database in LocalDB and signs you in as diego.perez@enermech.com, without Cloudflare
cd src\PresApp.Api
dotnet run

# 2. Front end, in another terminal (http://localhost:5173, live reload)
cd web
npm install
npm run dev
```

- The development user is set in `src/PresApp.Api/appsettings.Development.json` (`Auth:DevelopmentUserEmail`). That mode exists only in the Development environment.
- Another database: create `src/PresApp.Api/appsettings.Local.json` (ignored by Git) with your own `ConnectionStrings:PresApp`.
- Tests: `dotnet test`.
- Data model changes: `dotnet ef migrations add <Name> -p src/PresApp.Infrastructure -s src/PresApp.Infrastructure -o Data/Migrations`. PresApp applies migrations by itself on start-up.

## Unit Excel

1. **Templates** – an administrator uploads each unit's clean client template in *Excel templates*
   (table `MasterList` with one empty row, `Dashboard`, `Categories`; no `Key` column, not protected).
   The first template of a unit also loads its dashboard categories into the app.
2. **Download** – *Excel* page of the unit. The file is built from the live data: one row per active
   equipment, hidden `Key` column, dropdowns, dashboard and categories. It is not protected.
3. **Upload** – the edited file is checked (session, age limit, table structure, Key column) and shown as
   a preview; *Apply* compares it again with the current data and saves the valid changes in one transaction.
   Each downloaded file can be applied once. Problems go to *Import issues*.
4. **Onboarding** – a client master list (any version) can be loaded on the same page: rows are matched by
   EQUIP NO, unknown tags become new equipment, empty cells never clear anything.

The structure check allows hidden columns and rows, filters and sorting; moving, renaming, inserting or
deleting columns, or editing the Key column, stops the import.

## Pipeline Register

Lines are assets of type Pipeline (discipline Piping, Equipment ID `PL-EMP-<unit>-###`). They have the same gates as equipment, plus Length (m), Size, Pipe spec (with its class and flange type), Service and P&ID number.

- The line number follows `unit-number-service-size"-spec[-insulation]`, e.g. `39-0218-LS-16"-61055-P`. Size (16, 3/4, 1-1/2, 2.5), spec and service are read from it when the line is created and again when the line number is changed. A trailing insulation code is accepted but not stored. The P&ID number is entered by hand.
- A line number that starts with another unit, or that cannot be read, is refused.
- **Paste a line list** (Pipeline Register): copy the Line No. column from Excel, optionally with the length in the next column. The check shows what each row becomes (to create, already there, cannot be read); nothing is created until you confirm.
- **Pipe specs** (Admin → Pipe specs): class and flange type of each spec, kept by hand or loaded from the client's pipe spec Excel (preview, then apply; nothing is deleted). Lines read them from the spec in their line number, so correcting a spec updates all its lines; a spec not in the list is flagged.
- **Line codes** (Admin → Line codes): meanings of the service designations from the client line numbering standard. A code not in the list is flagged, never guessed.
- **Client line list with data**: an .xlsx whose list has a "Line No." column, uploaded from Add lines in bulk. Same preview/apply as the client list onboarding: new line numbers are created, known columns (Length, gates, remarks, Current Activity…) are loaded, empty cells never clear anything.
- **Same line written differently**: a line is identified by unit, sequence, service, size and spec (the insulation code at the end and the size format do not count). A number that matches an existing line only this way is never created twice: the paste marks it "Check: same line?", the New line page asks for confirmation, and a client list holds it for review with the existing line's P&ID, circuit and length. In the preview you choose: leave it for review, same line (keep or change its number), or a different line.
- Lines are not sent to Simpro and are not counted in the Excel dashboard figures; the unit page shows their count, total length and how many are N2 blanketed.

## Monitoring and workpacks

- **Monitoring** (per unit): DURING = not marked as preserved yet; POST = on or after the date it was marked. A POST asset is *Overdue* when its last G4 N2 blanket reading is older than the interval (7 days by default), *No reading* when nothing was measured since it was preserved, and *Low N2* below the minimum (off until set). Both values are in the Monitoring settings at the bottom of the page (administrators).
- **Record a round**: one measurement time for many assets; only filled cells are saved, all together or nothing. Same rules as a single reading (latest values, Last Field Action Date, DURING/POST).
- **Workpacks and groups**: internal groups (Workpack, Circuit, System) of equipment, lines and E&I of one unit. A client list with a Circuit / Workpack / System column puts each row in that group (created if missing; an asset is in one group of each kind). A group can take all the items of another group (e.g. a workpack takes a circuit). Groups can be deactivated or deleted (the assets stay). Code generated as `WP-039-001` unless given. Add items from the group page or by selecting rows in a register ("Add to group"). Progress by gate counts only items still on site. Groups never go to the client Excel nor to Simpro.

## Lists

Admin → Lists: values of the dropdowns. Add, reorder, deactivate; never rename or delete (assets keep their text). The gate status list is fixed.

## Custom fields (E&I and any register)

- Admin → Custom fields: fields added to a register without code changes (text, number, date, list of options, yes/no), grouped by section on the asset page and optionally shown as register columns. Values are audited in the asset history.
- Client lists (onboarding upload) load a custom field when a column header matches the field name.
- A field with values cannot change its kind; fields and options are deactivated, never deleted.

## Server installation (once)

1. **Database.** In SSMS on EM-L-5236, run `deploy/sql/01-create-database.sql`. It creates the empty `PresApp` database. PresApp creates the tables on its first start; the service login is created by the install script (step 4), because the Windows account `NT SERVICE\PresApp` only exists once the service is installed.
2. **Backups.** Set the two folders at the top of `deploy/sql/02-backup-jobs.sql` and run it. It creates the SQL Agent job: full backup daily at 02:00, differential every 4 hours, 30-day retention, copy to a second location.
3. **Publish.** On the development machine: `.\deploy\publish.ps1`. Copy the `publish` folder to the server.
4. **Service.** On the server, as administrator: `.\deploy\install-service.ps1`. It installs the `PresApp` service (automatic start, restart on failure), gives `NT SERVICE\PresApp` its SQL login on the database (needs `sqlcmd`) and creates `C:\PresApp\app\appsettings.Production.json`.
5. **Cloudflare** (next section). Fill in `Auth` and `Cloudflare` in `appsettings.Production.json` and restart the service.
6. **Emergency access.** On the server: `C:\PresApp\app\PresApp.Api.exe set-emergency-password diego.perez@enermech.com`. It is used by opening `http://127.0.0.1:5081` on the server itself when the internet or Cloudflare is down.

To update to a new version, repeat steps 3 and 4. The script backs up the database before copying.

## Cloudflare

Menu names may differ slightly in the Cloudflare Zero Trust dashboard.

1. **Tunnel:** in Zero Trust → Networks → Tunnels, create a tunnel `presapp` and install the connector on the server with the command the dashboard gives (it becomes a Windows service). Add the public hostname `presapp.<domain>` → `http://127.0.0.1:5080`. Never publish port 5081. Example file: `deploy/cloudflared/config.example.yml`.
2. **Sign-in by code:** in Settings → Authentication, enable *One-time PIN*.
3. **User group:** in Access → Access groups, create `PresApp users`. PresApp keeps its e-mail list.
4. **Application:** in Access → Applications, add a *Self-hosted* application for `presapp.<domain>` with a 1-month session and an *Allow* policy that includes the `PresApp users` group. Copy the **Application Audience (AUD) tag**.
5. **Configure PresApp** in `appsettings.Production.json`:
   - `Auth:CloudflareTeamDomain` = `<team>.cloudflareaccess.com`
   - `Auth:CloudflareAudience` = the AUD tag
   - `Cloudflare:AccountId`, `Cloudflare:AccessGroupId` and `Cloudflare:ApiToken` (a token with *Access: Organizations, Identity Providers, and Groups — Edit*). With these, adding or deactivating a user in PresApp updates the Cloudflare list and revokes their sessions. Without them, the list is kept by hand in Cloudflare.

## Security in short

- One sign-in: a code sent to the e-mail (Cloudflare). PresApp checks the token signature and looks the e-mail up among its active users.
- Roles: Viewer, Field Technician, Engineer, Admin. Every endpoint requires its permission on the server.
- Every field change is written to `ChangeLog` (who, when, before, after, source) in the same transaction as the change.
- Optimistic concurrency (`rowversion`): nobody overwrites another person's change without knowing.
