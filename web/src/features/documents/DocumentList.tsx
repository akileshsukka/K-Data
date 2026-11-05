import { ApiError } from "../../api/client";
import { Perm, type StoredDocument } from "../../api/types";
import { useSession } from "../../app/session";
import { useToast } from "../../components/Toast";
import { formatDateTime } from "../../components/dates";
import { documentUrl, formatSize, useDeleteDocument, useUnlinkDocument } from "./useDocuments";

const gateText = (gate: number | null, step = "G") => (gate ? `${step}${gate}` : "General");

/**
 * Signed documents of one asset (optionally one gate). The name opens the file in a new tab
 * (PDF and photos show in the browser); "Download" saves it. Links to other gates or assets are shown so
 * nobody uploads the same certificate twice.
 */
export function DocumentList({ docs, assetId, gate, showGates = false, step = "G" }: {
  docs: StoredDocument[];
  assetId: string;
  /** Only documents of this gate (undefined = all of the asset). */
  gate?: number | null;
  showGates?: boolean;
  /** Gate prefix of this asset: "E" for E&I items (E1–E8). Gates of other assets keep G. */
  step?: string;
}) {
  const session = useSession();
  const toast = useToast();
  const unlink = useUnlinkDocument();
  const remove = useDeleteDocument();
  const mine = (d: StoredDocument) => d.links.filter((l) => l.assetId === assetId && (gate === undefined || l.gate === gate));
  const shown = docs.filter((d) => mine(d).length > 0);
  if (shown.length === 0) return null;

  async function takeOff(d: StoredDocument) {
    const links = mine(d);
    const where = gate === undefined ? "this asset" : gateText(gate ?? null, step);
    const last = d.links.length === links.length;
    if (!window.confirm(`Remove "${d.fileName}" from ${where}?${last ? " It is not linked anywhere else, so the document is removed (an administrator can recover the file)." : ""}\nThe documentation status is not changed: update it by hand if needed.`)) return;
    try {
      for (const l of links) await unlink.mutateAsync({ documentId: d.id, linkId: l.linkId });
      toast("Document removed");
    } catch (err) {
      toast(err instanceof ApiError ? err.message : "Could not remove it.", "error");
    }
  }

  async function deleteEverywhere(d: StoredDocument) {
    if (!window.confirm(`Delete "${d.fileName}" from every gate and asset it is linked to?`)) return;
    try {
      await remove.mutateAsync(d.id);
      toast("Document deleted");
    } catch (err) {
      toast(err instanceof ApiError ? err.message : "Could not delete it.", "error");
    }
  }

  return (
    <ul className="divide-y divide-line/70">
      {shown.map((d) => {
        const elsewhere = d.links.filter((l) => !mine(d).includes(l));
        return (
          <li key={d.id} className="flex flex-wrap items-start gap-x-4 gap-y-1 py-2">
            <span className={`mt-0.5 rounded px-1.5 py-0.5 text-xs font-bold ${d.contentType === "application/pdf" ? "bg-hold/10 text-hold" : "bg-violet-soft text-violet"}`}>
              {d.contentType === "application/pdf" ? "PDF" : "PHOTO"}
            </span>
            <div className="min-w-0 flex-1">
              <a href={documentUrl(d.id)} target="_blank" rel="noopener" className="font-semibold [overflow-wrap:anywhere] text-violet hover:underline">
                {d.fileName}
              </a>
              <p className="text-sm text-muted">
                {formatSize(d.sizeBytes)} · {d.uploadedBy} · {formatDateTime(d.uploadedAtUtc)}
                {showGates && ` · ${mine(d).map((l) => gateText(l.gate, step)).join(", ")}`}
              </p>
              {d.note && <p className="text-sm">{d.note}</p>}
              {elsewhere.length > 0 && (
                <p className="text-sm text-muted">
                  Also for: {elsewhere.map((l) => `${l.assetId === assetId ? "" : `${l.assetTag ?? l.assetCode} `}${gateText(l.gate, l.assetId === assetId ? step : "G")}`).join(" · ")}
                </p>
              )}
            </div>
            <div className="flex items-center gap-3 text-sm font-semibold">
              <a href={documentUrl(d.id, true)} download className="text-violet hover:underline">Download</a>
              {session.can(Perm.documentsUpload) && (
                <button type="button" className="text-muted hover:text-hold" disabled={unlink.isPending} onClick={() => takeOff(d)}>Remove</button>
              )}
              {session.can(Perm.documentsDelete) && elsewhere.length > 0 && (
                <button type="button" className="text-muted hover:text-hold" disabled={remove.isPending} onClick={() => deleteEverywhere(d)}>Delete everywhere</button>
              )}
            </div>
          </li>
        );
      })}
    </ul>
  );
}
