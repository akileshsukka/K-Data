import type { ButtonHTMLAttributes } from "react";

type Variant = "primary" | "secondary" | "danger" | "quiet";

const styles: Record<Variant, string> = {
  primary: "bg-violet text-white hover:bg-violet-dark disabled:bg-violet/50",
  secondary: "bg-panel text-ink border border-line hover:border-violet hover:text-violet disabled:text-muted",
  danger: "bg-panel text-hold border border-hold/40 hover:bg-hold hover:text-white",
  quiet: "text-violet hover:bg-violet-soft",
};

export function Button({
  variant = "primary",
  busy = false,
  className = "",
  children,
  disabled,
  ...rest
}: ButtonHTMLAttributes<HTMLButtonElement> & { variant?: Variant; busy?: boolean }) {
  return (
    <button
      {...rest}
      disabled={disabled || busy}
      className={`inline-flex min-h-10 items-center justify-center gap-2 rounded-md px-4 text-[15px] font-semibold transition-colors disabled:cursor-not-allowed ${styles[variant]} ${className}`}
    >
      {busy && <span className="size-4 animate-spin rounded-full border-2 border-current border-r-transparent" aria-hidden />}
      {children}
    </button>
  );
}
