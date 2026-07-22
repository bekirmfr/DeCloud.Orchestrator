// The failure taxonomy from DESIGN §5. Three KINDS, because each deserves
// different UI. Every call site should be able to switch on `kind` and know
// exactly how to treat the failure — no ad-hoc guessing.

export type FailureKind =
  | "cancel" // user rejected a signature/tx (ACTION_REJECTED / 4001). NOT an error — return to prior state quietly.
  | "uncertain" // RPC blip, transport failure, refresh returned null. Keep last-known, mark stale, retry. Never destroy state.
  | "definitive"; // server rejected, tx failed on-chain, validation. Clear, actionable error.

export class AppError extends Error {
  readonly kind: FailureKind;
  readonly status?: number; // HTTP status, when applicable
  readonly cause?: unknown;

  constructor(kind: FailureKind, message: string, opts?: { status?: number; cause?: unknown }) {
    super(message);
    this.name = "AppError";
    this.kind = kind;
    this.status = opts?.status;
    this.cause = opts?.cause;
  }
}

export const cancelled = (message = "Cancelled by user", cause?: unknown) =>
  new AppError("cancel", message, { cause });

export const uncertain = (message = "Could not verify — will retry", cause?: unknown) =>
  new AppError("uncertain", message, { cause });

export const definitive = (message: string, status?: number, cause?: unknown) =>
  new AppError("definitive", message, { status, cause });

/**
 * Map a wallet/provider throw to a FailureKind.
 * A rejected signature (EIP-1193 code 4001 / ethers "ACTION_REJECTED") is a
 * CANCEL, not an error — this is the classic mistake to get right once, here.
 */
export function classifyWalletError(err: unknown): AppError {
  const code = (err as { code?: unknown })?.code;
  const ethersCode = (err as { code?: string })?.code;
  if (code === 4001 || ethersCode === "ACTION_REJECTED") {
    return cancelled("Signature request was rejected", err);
  }
  // Network/timeout style provider errors are uncertain; everything else definitive.
  const msg = (err as Error)?.message ?? "Wallet error";
  if (/network|timeout|underlying|could not detect/i.test(msg)) return uncertain(msg, err);
  return definitive(msg, undefined, err);
}
