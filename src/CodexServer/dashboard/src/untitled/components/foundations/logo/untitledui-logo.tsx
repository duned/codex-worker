import { Server01 } from "@untitledui/icons";
export const UntitledLogo = ({ className }: { className?: string }) => <div className={`flex items-center gap-2 text-lg font-semibold text-primary ${className ?? ""}`}><Server01 aria-hidden="true" className="size-7 text-fg-brand-primary" /><span>Codex Server</span></div>;
