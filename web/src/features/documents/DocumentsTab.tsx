import { useState } from "react";
import { Perm, type AssetProfile } from "../../api/types";
import { useSession } from "../../app/session";
import { Button } from "../../components/Button";
import { gatePrefix } from "../../components/GateStatus";
import { DocumentList } from "./DocumentList";
import { UploadDocumentDialog } from "./UploadDocumentDialog";
import { useAssetDocuments } from "./useDocuments";

/** Every signed document of the asset: general ones and those of each gate. */
export function DocumentsTab({ asset }: { asset: AssetProfile }) {
  const session = useSession();
  const documents = useAssetDocuments(asset.id);
  const [uploading, setUploading] = useState(false);
  const canUpload = session.can(Perm.documentsUpload) && (asset.isActive || session.can("assets.manage"));
  const docs = documents.data ?? [];
  const groups: { gate: number | null; title: string }[] = [
    { gate: null, title: "General" },
    ...Array.from({ length: 8 }, (_, i) => ({ gate: i + 1, title: `${gatePrefix(asset.type)}${i + 1}` })),
  ];

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <p className="text-muted">Signed certificates and photos of signed sheets. Click a name to open it.</p>
        {canUpload && <Button onClick={() => setUploading(true)}>Upload signed document</Button>}
      </div>
      {documents.isPending ? (
        <p className="text-muted">Loading…</p>
      ) : documents.error ? (
        <p className="text-hold">{documents.error.message}</p>
      ) : docs.length === 0 ? (
        <p className="rounded-lg border border-dashed border-line px-4 py-6 text-center text-muted">No documents yet.</p>
      ) : (
        groups
          .filter((g) => docs.some((d) => d.links.some((l) => l.assetId === asset.id && l.gate === g.gate)))
          .map((g) => (
            <section key={g.title} className="rounded-lg border border-line bg-panel px-4 py-3">
              <h2 className="text-lg font-semibold">{g.title}</h2>
              <DocumentList docs={docs} assetId={asset.id} gate={g.gate} step={gatePrefix(asset.type)} />
            </section>
          ))
      )}
      {canUpload && <UploadDocumentDialog asset={asset} open={uploading} onClose={() => setUploading(false)} />}
    </div>
  );
}
