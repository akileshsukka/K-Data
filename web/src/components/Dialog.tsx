import { useEffect, useRef, type ReactNode } from "react";

/** Native modal dialog: focus trap, Esc to close and backdrop come from the browser. */
export function Dialog({ open, title, onClose, children }: { open: boolean; title: string; onClose: () => void; children: ReactNode }) {
  const ref = useRef<HTMLDialogElement>(null);

  useEffect(() => {
    const dialog = ref.current;
    if (!dialog) return;
    if (open && !dialog.open) dialog.showModal();
    if (!open && dialog.open) dialog.close();
  }, [open]);

  return (
    <dialog
      ref={ref}
      onClose={onClose}
      onCancel={onClose}
      className="m-auto w-[min(32rem,calc(100vw-2rem))] rounded-lg border border-line bg-panel p-0 text-text shadow-[0_24px_64px_-24px_rgba(30,27,75,0.45)] backdrop:bg-ink/50"
    >
      {open && (
        <div className="p-6">
          <h2 className="text-2xl font-semibold">{title}</h2>
          <div className="mt-4">{children}</div>
        </div>
      )}
    </dialog>
  );
}
