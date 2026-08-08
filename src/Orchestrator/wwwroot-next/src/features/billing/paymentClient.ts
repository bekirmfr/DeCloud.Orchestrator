import { Contract, parseUnits, formatUnits, type Signer } from "ethers";
import type { DepositInfoResponse } from "./useBalance";

// Phase 6 · Slice 2. On-chain money flows, ported faithfully from the legacy
// payment.js. The orchestrator never moves funds — these talk to the escrow
// directly via the wallet signer.
//
// SAFETY — every guard from the reference is preserved:
//   • frozen()/replacementContract — never transact against a migrated contract.
//   • minimum deposit, USDC balance, allowance-then-approve, real decimals.
//   • EIP-1559 gas floors tuned for Polygon (×1.2, min 30 gwei, 50/30 fallback).
//   • network match — fail CLOSED (ask the user to switch) rather than auto-add
//     a chain with guessed params.
// The wallet is the final gate: it shows amount + contract before the user signs.
// This code cannot be executed here — VERIFY ON POLYGON AMOY BEFORE MAINNET.

const USDC_ABI = [
  "function approve(address spender, uint256 amount) returns (bool)",
  "function allowance(address owner, address spender) view returns (uint256)",
  "function balanceOf(address account) view returns (uint256)",
  "function decimals() view returns (uint8)",
];
const ESCROW_ABI = [
  "function deposit(uint256 amount)",
  "function withdrawBalance(uint256 amount)",
  "function userBalances(address) view returns (uint256)",
  "function nodePendingPayouts(address) view returns (uint256)",
  "function nodeWithdraw(uint256 amount)",
  "function frozen() view returns (bool)",
  "function replacementContract() view returns (address)",
];

export interface TxProgress {
  message: string;
  txHash?: string;
}

// ethers v6 types Contract methods dynamically (possibly-undefined), so we cast
// to explicit shapes. Overrides carry the EIP-1559 gas fields.
type Overrides = { maxFeePerGas: bigint; maxPriorityFeePerGas: bigint };
interface ContractTx { hash: string; wait(): Promise<unknown> }
interface UsdcContract {
  approve(spender: string, amount: bigint, overrides?: Overrides): Promise<ContractTx>;
  allowance(owner: string, spender: string): Promise<bigint>;
  balanceOf(account: string): Promise<bigint>;
  decimals(): Promise<bigint>;
}
interface EscrowContract {
  deposit(amount: bigint, overrides?: Overrides): Promise<ContractTx>;
  withdrawBalance(amount: bigint, overrides?: Overrides): Promise<ContractTx>;
  userBalances(addr: string): Promise<bigint>;
  nodePendingPayouts(addr: string): Promise<bigint>;
  nodeWithdraw(amount: bigint | number, overrides?: Overrides): Promise<ContractTx>;
  frozen(): Promise<boolean>;
  replacementContract(): Promise<string>;
}

function contracts(signer: Signer, config: DepositInfoResponse): { usdc: UsdcContract; escrow: EscrowContract } {
  return {
    usdc: new Contract(config.usdcTokenAddress, USDC_ABI, signer) as unknown as UsdcContract,
    escrow: new Contract(config.escrowContractAddress, ESCROW_ABI, signer) as unknown as EscrowContract,
  };
}

/** Never transact against a migrated contract — funds would be stranded. */
async function assertNotFrozen(escrow: EscrowContract): Promise<void> {
  const frozen = await escrow.frozen();
  if (!frozen) return;
  let replacement = "";
  try { replacement = await escrow.replacementContract(); } catch { /* best-effort */ }
  throw new Error(
    `The escrow contract has been migrated${replacement ? ` to ${replacement}` : ""}. ` +
    `Use the classic app to move funds to the new contract.`
  );
}

/** Fail closed on the wrong network — the user switches in their wallet. */
async function assertNetwork(signer: Signer, config: DepositInfoResponse): Promise<void> {
  const net = await signer.provider!.getNetwork();
  if (net.chainId.toString() !== String(config.chainId)) {
    throw new Error(`Wrong network — switch your wallet to ${config.chainName} (chain ${config.chainId}) and retry.`);
  }
}

/** EIP-1559 gas with Polygon floors. Ported verbatim from payment.js getGasPrice. */
async function gasOverrides(signer: Signer): Promise<Overrides> {
  const MIN = parseUnits("30", "gwei");
  const FALLBACK_MAX = parseUnits("50", "gwei");
  const FALLBACK_PRIO = parseUnits("30", "gwei");
  try {
    const fee = await signer.provider!.getFeeData();
    if (fee.maxFeePerGas != null && fee.maxPriorityFeePerGas != null) {
      let maxFee = (fee.maxFeePerGas * 120n) / 100n;
      let prio = (fee.maxPriorityFeePerGas * 120n) / 100n;
      if (prio < MIN) prio = FALLBACK_PRIO;
      if (maxFee < MIN) maxFee = FALLBACK_MAX;
      if (maxFee < prio) maxFee = prio;
      return { maxFeePerGas: maxFee, maxPriorityFeePerGas: prio };
    }
  } catch { /* fall through to fallback */ }
  return { maxFeePerGas: FALLBACK_MAX, maxPriorityFeePerGas: FALLBACK_PRIO };
}

/** Deposit USDC into the escrow (approve if needed, then deposit). */
export async function depositUSDC(
  signer: Signer,
  config: DepositInfoResponse,
  amount: string,
  onProgress: (p: TxProgress) => void = () => {},
): Promise<{ txHash: string }> {
  const { usdc, escrow } = contracts(signer, config);

  onProgress({ message: "Checking network…" });
  await assertNetwork(signer, config);
  await assertNotFrozen(escrow);

  const amountNum = parseFloat(amount);
  if (isNaN(amountNum) || amountNum <= 0) throw new Error("Enter a valid amount.");
  const min = config.minDeposit || 1;
  if (amountNum < min) throw new Error(`Minimum deposit is ${min} USDC.`);

  const gas = await gasOverrides(signer);
  const decimals = Number(await usdc.decimals());
  const amountWei = parseUnits(amount, decimals);

  onProgress({ message: "Checking USDC balance…" });
  const owner = await signer.getAddress();
  const held = await usdc.balanceOf(owner);
  if (held < amountWei) {
    throw new Error(`Insufficient USDC — you have ${formatUnits(held, decimals)}, need ${amount}.`);
  }

  const allowance = await usdc.allowance(owner, config.escrowContractAddress);
  if (allowance < amountWei) {
    onProgress({ message: "Approve USDC spend — confirm in your wallet." });
    const approveTx = await usdc.approve(config.escrowContractAddress, amountWei, gas);
    onProgress({ message: "Waiting for approval…", txHash: approveTx.hash });
    await approveTx.wait();
  }

  onProgress({ message: "Deposit — confirm in your wallet." });
  const tx = await escrow.deposit(amountWei, gas);
  onProgress({ message: "Waiting for deposit confirmation…", txHash: tx.hash });
  await tx.wait();
  onProgress({ message: "Deposit confirmed.", txHash: tx.hash });
  return { txHash: tx.hash };
}

/** Withdraw earned payouts (node/template revenue). 0 = full balance (v3 contract). */
export async function withdrawEarnings(
  signer: Signer,
  config: DepositInfoResponse,
  onProgress: (p: TxProgress) => void = () => {},
): Promise<{ txHash: string }> {
  const { escrow } = contracts(signer, config);
  await assertNetwork(signer, config);
  await assertNotFrozen(escrow);
  const gas = await gasOverrides(signer); 
  onProgress({ message: "Withdraw earnings — confirm in your wallet." });
  const tx = await escrow.nodeWithdraw(0, gas);
  onProgress({ message: "Waiting for confirmation…", txHash: tx.hash });
  await tx.wait();
  onProgress({ message: "Withdrawal confirmed.", txHash: tx.hash });
  return { txHash: tx.hash };
}

/** On-chain reads for display: deposit balance + pending payout, in USDC units. */
export async function readOnChain(
  signer: Signer,
  config: DepositInfoResponse,
): Promise<{ userBalance: number; pendingPayout: number }> {
  const { usdc, escrow } = contracts(signer, config);
  const addr = await signer.getAddress();
  const decimals = Number(await usdc.decimals());
  const [ub, pp] = await Promise.all([
    escrow.userBalances(addr),
    escrow.nodePendingPayouts(addr),
  ]);
  return {
    userBalance: Number(formatUnits(ub, decimals)),
    pendingPayout: Number(formatUnits(pp, decimals)),
  };
}
