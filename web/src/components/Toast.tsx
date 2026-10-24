import { createContext, useCallback, useContext, useState, type ReactNode } from "react";

type Tone = "success" | "error" | "info";
interface ToastItem {
  id: number;
  tone: Tone;
  text: string;
}

const ToastContext = createContext<(text: string, tone?: Tone) => void>(() => {});

export const useToast = () => useContext(ToastContext);

const toneStyle: Record<Tone, string> = {
  success: "border-l-done",
  error: "border-l-hold",
  info: "border-l-violet",
};

/** Short confirmations of what just happened ("Usuario creado"). Errors stay longer. */
export function ToastProvider({ children }: { children: ReactNode }) {
  const [items, setItems] = useState<ToastItem[]>([]);

  const push = useCallback((text: string, tone: Tone = "success") => {
    const id = Date.now() + Math.random();
    setItems((list) => [...list, { id, tone, text }]);
    setTimeout(() => setItems((list) => list.filter((t) => t.id !== id)), tone === "error" ? 9000 : 4500);
  }, []);

  return (
    <ToastContext.Provider value={push}>
      {children}
      <div className="pointer-events-none fixed inset-x-4 bottom-20 z-50 flex flex-col items-center gap-2 lg:bottom-6" role="status" aria-live="polite">
        {items.map((t) => (
          <div key={t.id} className={`pointer-events-auto w-full max-w-md rounded-md border border-line border-l-4 bg-panel px-4 py-3 shadow-lg ${toneStyle[t.tone]}`}>
            {t.text}
          </div>
        ))}
      </div>
    </ToastContext.Provider>
  );
}
