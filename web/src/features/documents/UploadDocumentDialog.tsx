import { useState } from "react";
import { ApiError } from "../../api/client";
import type { AssetProfile, SearchHit } from "../../api/types";
import { AssetSearch } from "../../components/AssetSearch";
import { Button } from "../../components/Button";
import { Dialog } from "../../components/Dialog";
import { Field, TextInput } from "../../components/Field";
import { useToast } from "../../components/Toast";
import { MAX_FILE_MB, formatSize, useUploadDocument, type UploadInput } from "./useDocuments";

const ACCEPT = "application/pdf,.pdf,image/jpeg,image/png,image/heic,image/heif,image/webp,.jpg,.jpeg,.png,.heic,.heif,.webp";
const GATES = [1, 2, 3, 4, 5, 6, 7, 8];

/**
 * Upload a signed PDF or a photo of a signed sheet. It can cover several gates of this asset and, when one
 * certificate covers several equipment, other assets too (same gates). Each gate it covers is marked Received.
 */
export function UploadDocumentDialog({ asset, open, onClose, initialGates = [] }: {
  asset: AssetProfile;
  open: boolean;
  onClose: () => void;
  initialGates?: number[];
}) {
  return (
    <Dialog open={open} title="Upload signed document" onClose={onClose}>
      {/* Mounted only while open, so every opening starts clean with the gate it was opened from. */}
      {open && <UploadForm asset={asset} initialGates={initialGates} onClose={onClose} />}
    </Dialog>
  );
}

function UploadForm({ asset, initialGates, onClose }: { asset: AssetProfile; initialGates: number[]; onClose: () => void }) {
  const toast = useToast();
  const upload = useUploadDocument();
  const [file, setFile] = useState<File | null>(null);
  const [gates, setGates] = useState<number[]>(initialGates);
  const [others, setOthers] = useState<SearchHit[]>([]);
  const [note, setNote] = useState("");
  const [error, setError] = useState<string | null>(null);

  function pickFile(f: File | null) {
    setError(null);
    if (f && f.size > MAX_FILE_MB * 1024 * 1024) {
      setError(`The file is ${formatSize(f.size)}; the limit is ${MAX_FILE_MB} MB.`);
      setFile(null);
      return;
    }
    setFile(f);
  }

  const toggle = (g: number) => setGates((cur) => (cur.includes(g) ? cur.filter((x) => x !== g) : [...cur, g].sort()));

  async function submit() {
    if (!file) { setError("Choose a PDF or a photo."); return; }
    const targets = [asset.id, ...others.map((o) => o.id)];
    const links = targets.flatMap((assetId): UploadInput["links"] =>
      gates.length ? gates.map((gate) => ({ assetId, gate })) : [{ assetId, gate: null }]);
    try {
      await upload.mutateAsync({ file, note: note.trim() || null, links });
      toast(gates.length ? `Uploaded. ${gates.map((g) => `G${g}`).join(", ")} documentation marked as Received.` : "Uploaded.");
      onClose();
    } catch (err) {
      setError(err instanceof ApiError ? err.message : "Could not upload the file.");
    }
  }

  return (
    <div className="space-y-4">
      <Field label="File" hint={`PDF or photo (JPG, PNG, HEIC), up to ${MAX_FILE_MB} MB. On a phone you can take the photo directly.`}>
        <input
          type="file"
          accept={ACCEPT}
          onChange={(e) => pickFile(e.target.files?.[0] ?? null)}
          className="mt-1 block w-full text-[15px] file:mr-3 file:min-h-10 file:rounded-md file:border file:border-line file:bg-panel file:px-4 file:font-semibold file:text-ink hover:file:border-violet"
        />
      </Field>

      <fieldset>
        <legend className="text-sm font-semibold text-ink">Gates of {asset.values.Tag ?? asset.code} it covers</legend>
        <div className="mt-1 flex flex-wrap gap-1.5">
          {GATES.map((g) => (
            <button
              key={g}
              type="button"
              aria-pressed={gates.includes(g)}
              onClick={() => toggle(g)}
              className={`min-h-9 min-w-12 rounded-md border px-3 font-semibold ${gates.includes(g) ? "border-violet bg-violet text-white" : "border-line bg-panel text-ink hover:border-violet"}`}
            >
              G{g}
            </button>
          ))}
        </div>
        <p className="mt-1 text-sm text-muted">
          {gates.length ? "Each selected gate is marked as documentation Received (you can change it afterwards)." : "No gate selected: a general document of the asset."}
        </p>
      </fieldset>

      <div>
        <span className="text-sm font-semibold text-ink">Also covers other equipment (optional)</span>
        <div className="mt-1">
          <AssetSearch
            placeholder="Add another tag covered by this document"
            excludeIds={new Set([asset.id, ...others.map((o) => o.id)])}
            onPick={(h) => setOthers((cur) => [...cur, h])}
          />
        </div>
        {others.length > 0 && (
          <ul className="mt-2 flex flex-wrap gap-1.5">
            {others.map((o) => (
              <li key={o.id} className="inline-flex items-center gap-1 rounded-md border border-line px-2 py-1 text-sm">
                {o.tag ?? o.code}
                <button type="button" aria-label={`Remove ${o.tag ?? o.code}`} className="px-1 text-muted hover:text-hold" onClick={() => setOthers((cur) => cur.filter((x) => x.id !== o.id))}>×</button>
              </li>
            ))}
          </ul>
        )}
        {others.length > 0 && <p className="mt-1 text-sm text-muted">They get the same gates selected above.</p>}
      </div>

      <Field label="Note (optional)">
        <TextInput value={note} maxLength={1000} onChange={(e) => setNote(e.target.value)} placeholder="e.g. Signed by the client representative" />
      </Field>

      {error && <p className="text-sm text-hold" role="alert">{error}</p>}

      <div className="flex justify-end gap-2">
        <Button variant="secondary" onClick={onClose}>Cancel</Button>
        <Button busy={upload.isPending} disabled={!file} onClick={submit}>Upload</Button>
      </div>
    </div>
  );
}
