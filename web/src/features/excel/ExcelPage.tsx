import { useState } from "react";
import { Link, useNavigate, useParams } from "react-router";
import { useQuery } from "@tanstack/react-query";
import { api, upload } from "../../api/client";
import { Perm, type ImportRunDetail, type UnitExcelOverview } from "../../api/types";
import { useSession } from "../../app/session";
import { useUnit } from "../../app/useCatalog";
import { PageHeader } from "../../components/PageHeader";
import { formatDateTime } from "../../components/dates";
import { useToast } from "../../components/Toast";
import { DownloadExcelButton } from "./DownloadExcelButton";
import { FileDrop } from "./FileDrop";
import { StatusChip } from "./ImportBits";

/** Download the unit file, upload it back (preview first), and see what happened to earlier files. */
export function ExcelPage() {
  const unitId = Number(useParams().unitId);
  const { unit } = useUnit(unitId);
  const session = useSession();
  const navigate = useNavigate();
  const toast = useToast();
  const [busy, setBusy] = useState<"update" | "onboarding" | null>(null);
  const overview = useQuery({
    queryKey: ["excel", unitId],
    queryFn: ({ signal }) => api<UnitExcelOverview>(`/api/units/${unitId}/excel`, { signal }),
  });

  async function send(file: File, kind: "update" | "onboarding") {
    setBusy(kind);
    try {
      const detail = await upload<ImportRunDetail>(`/api/units/${unitId}/excel/${kind === "update" ? "import" : "onboarding"}`, file);
      navigate(`/imports/${detail.run.id}`);
    } catch (e) {
      toast((e as Error).message, "error");
    } finally {
      setBusy(null);
    }
  }

  const o = overview.data;
  const canImport = session.can(Perm.excelImport);
  return (
    <div>
      <PageHeader title={`${unit?.name ?? "Unit"} Excel`} description="The client Master Equipment List, built from the live data. Engineers edit it and it comes back here." />

      <div className="mt-6 grid gap-6 lg:grid-cols-2">
        <section className="rounded-lg border border-line bg-panel p-5">
          <h2 className="text-xl font-semibold">1. Download</h2>
          {o && !o.hasTemplate ? (
            <p className="mt-2 text-muted">
              This unit has no Excel template yet.{" "}
              {session.can(Perm.catalogManage) ? <Link to="/admin/excel-templates" className="font-semibold text-violet">Upload it in Excel templates.</Link> : "Ask an administrator to upload it."}
            </p>
          ) : (
            <>
              <p className="mt-2 text-muted">
                One row per active equipment, dashboard and categories included. Engineers edit the status, gate and remark columns and may hide columns, but must not move, rename,
                insert or delete them. A file can be uploaded back once, within {o?.maxAgeHours ?? 48} hours.
              </p>
              <div className="mt-4"><DownloadExcelButton unitId={unitId} label={`Download ${unit?.name ?? ""} Excel`} /></div>
              {o?.templateFileName && <p className="mt-3 text-sm text-muted">Template: {o.templateFileName}, uploaded {formatDateTime(o.templateUploadedAtUtc)}</p>}
            </>
          )}
          <div className="mt-5 border-t border-line pt-4">
            <h3 className="font-semibold">Piping line list</h3>
            <p className="mt-1 text-muted">Line List and Summary of the active lines, in the engineers' layout. It is uploaded back here, like the unit Excel.</p>
            <div className="mt-3"><DownloadExcelButton unitId={unitId} variant="secondary" piping label={`Download ${unit?.name ?? ""} line list`} /></div>
          </div>
        </section>

        <section className="rounded-lg border border-line bg-panel p-5">
          <h2 className="text-xl font-semibold">2. Upload the edited file</h2>
          {canImport ? (
            <div className="mt-3">
              <FileDrop busy={busy === "update"} label="Choose the edited file" onFile={(f) => send(f, "update")}
                hint="You will see every change before anything is saved." />
            </div>
          ) : (
            <p className="mt-2 text-muted">Edited files are uploaded by an administrator.</p>
          )}
        </section>
      </div>

      <section className="mt-6 rounded-lg border border-line bg-panel">
        <h2 className="px-5 pt-5 text-xl font-semibold">Uploads</h2>
        {o && o.runs.length === 0 && <p className="px-5 pt-2 pb-5 text-muted">No file uploaded yet for this unit.</p>}
        {o && o.runs.length > 0 && (
          <ul className="mt-3">
            {o.runs.map((r) => (
              <li key={r.id} className="border-t border-line">
                <Link to={`/imports/${r.id}`} className="grid gap-1 px-5 py-3 hover:bg-surface sm:grid-cols-[1fr_auto] sm:items-center">
                  <span className="min-w-0">
                    <span className="block truncate font-semibold text-ink">{r.kind === "Onboarding" && "Client list · "}{r.fileName}</span>
                    <span className="block text-sm text-muted">
                      {formatDateTime(r.uploadedAtUtc)}{r.uploadedBy && ` by ${r.uploadedBy}`}
                      {r.status !== "Rejected" && ` · ${r.counts.applied + r.counts.derived + r.counts.created} changes, ${r.counts.issues} issues`}
                    </span>
                  </span>
                  <StatusChip status={r.status} />
                </Link>
              </li>
            ))}
          </ul>
        )}
      </section>

      <section className="mt-6 rounded-lg border border-line bg-panel">
        <h2 className="px-5 pt-5 text-xl font-semibold">Downloads</h2>
        {o && o.sessions.length === 0 && <p className="px-5 pt-2 pb-5 text-muted">No file downloaded yet.</p>}
        {o && o.sessions.length > 0 && (
          <div className="mt-3 overflow-x-auto">
            <table className="w-full min-w-[36rem] text-left">
              <thead className="text-sm text-muted">
                <tr>{["Downloaded", "By", "Rows", "Status"].map((h) => <th key={h} className="border-y border-line px-5 py-2 font-semibold">{h}</th>)}</tr>
              </thead>
              <tbody>
                {o.sessions.map((s) => {
                  const expired = s.status === "Open" && Date.now() - Date.parse(s.exportedAtUtc.endsWith("Z") ? s.exportedAtUtc : s.exportedAtUtc + "Z") > o.maxAgeHours * 3_600_000;
                  return (
                    <tr key={s.id} className="border-b border-line last:border-0">
                      <td className="px-5 py-2.5">{formatDateTime(s.exportedAtUtc)}</td>
                      <td className="px-5 py-2.5">{s.exportedBy}</td>
                      <td className="px-5 py-2.5 tabular-nums">{s.legacy ? "—" : s.rowCount}</td>
                      <td className="px-5 py-2.5 text-sm">
                        {s.status === "Processed" ? `Imported ${formatDateTime(s.processedAtUtc)}` : s.legacy ? <span className="text-muted">Power App file</span> : expired ? <span className="text-muted">Expired</span> : <span className="font-semibold text-done">Can be uploaded</span>}
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
        )}
      </section>

      {session.can(Perm.assetsManage) && canImport && (
        <details className="mt-6 rounded-lg border border-line bg-panel p-5">
          <summary className="cursor-pointer font-display text-xl font-semibold text-ink">Load a client list (onboarding)</summary>
          <p className="mt-2 max-w-3xl text-muted">
            For a unit's first load or a list sent by the client: rows are matched by EQUIP NO, unknown tags become new equipment with their Equipment ID,
            and filled cells complete or overwrite the data. Empty cells never clear anything. You will see a preview first.
          </p>
          <div className="mt-4 max-w-xl">
            <FileDrop busy={busy === "onboarding"} label="Choose the client list" onFile={(f) => send(f, "onboarding")} />
          </div>
        </details>
      )}
    </div>
  );
}
