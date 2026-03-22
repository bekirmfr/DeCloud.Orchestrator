# DeCloudEscrow Contract Migration Guide

This document describes the procedure for migrating from one `DeCloudEscrow`
deployment to a new one. Migration is only necessary when a contract-level
change cannot be handled by the existing admin functions (fee changes, platform
token launch, master node additions, staking activation — none of these require
migration).

**This contract has no upgrade proxy and no admin drain function.**
User funds are always safe. The migration process is voluntary — users who do
not act can still withdraw from the old contract indefinitely after it is frozen.

---

## When is migration necessary?

- Critical security vulnerability found post-audit
- New settlement logic that requires a different state layout
- Regulatory requirement mandating contract replacement

If in doubt, prefer adding authorized callers or adjusting parameters over
deploying a new contract.

---

## Migration overview

```
Old contract                          New contract
─────────────────                     ─────────────────────
1. Pause (optional)
2. Snapshot event logs          ──►   3. Deploy new contract
                                      4. Fund USDC reserves
                                      5. Call migrateBalances()
6. Call initiateFreeze()
   (3-day timelock starts)
   Users withdraw during window  ──►  Users re-deposit to new contract
7. Call executeFreeze()
   (old contract permanently frozen,
    withdrawals still open forever)
8. Update orchestrator config   ──►   New contract address live
```

Total operator downtime: ~5 minutes (deploy + migrateBalances + initiateFreeze).
User-facing impact: 3-day window where both contracts are accessible.

---

## Step-by-step procedure

### Prerequisites

- [Foundry](https://book.getfoundry.sh/) installed (`forge`, `cast`)
- `PRIVATE_KEY` environment variable set (owner wallet)
- `RPC_URL` environment variable set (Polygon RPC)
- Old contract address and deployment block number

```bash
export PRIVATE_KEY=0x...
export RPC_URL=https://polygon-rpc.com
export OLD_CONTRACT=0x...
export OLD_DEPLOY_BLOCK=12345678
export USDC_ADDRESS=0x3c499c542cEF5E3811e1192ce70d8cC03d5c3359  # Polygon USDC
```

---

### Step 1 — Pause old contract (optional but recommended)

Pausing stops new settlements while you take the snapshot. Users can still
withdraw during a pause.

```bash
cast send $OLD_CONTRACT "pause()" \
  --private-key $PRIVATE_KEY \
  --rpc-url $RPC_URL
```

---

### Step 2 — Snapshot event logs

Query all relevant events from the old contract and compute net balances.
Run this script from the `tools/` directory:

```bash
node snapshot-balances.js \
  --contract $OLD_CONTRACT \
  --from-block $OLD_DEPLOY_BLOCK \
  --rpc $RPC_URL \
  --out snapshot.json
```

> **Note:** `snapshot-balances.js` does not exist yet. Build it when needed.
> It should enumerate: `Deposited`, `UserWithdrawal`, `UsageReported`,
> `NodeWithdrawal`, and `PlatformTokenDeposited` events, then compute:
>
> ```
> userBalance[addr]  = Σ Deposited[addr]
>                    + Σ PlatformTokenDeposited.usdcEquivalent[addr]
>                    - Σ UserWithdrawal[addr]
>                    - Σ UsageReported.amount where user == addr
>
> nodeBalance[addr]  = Σ UsageReported.nodeShare where node == addr
>                    - Σ NodeWithdrawal[addr]
> ```
>
> Also read `storagePool` and `platformFees` directly from contract state.

Verify the snapshot total matches the old contract's USDC balance:

```bash
# Contract USDC balance
cast call $USDC_ADDRESS \
  "balanceOf(address)(uint256)" $OLD_CONTRACT \
  --rpc-url $RPC_URL

# Snapshot total (sum of all user + node + pool + fees)
cat snapshot.json | jq '.totalLiabilities'
```

These must match (or the snapshot total must be ≤ contract balance, accounting
for platform token USDC liability gap). Investigate any discrepancy before
proceeding.

---

### Step 3 — Deploy new contract

```bash
forge create contracts/DeCloudEscrow.sol:DeCloudEscrow \
  --rpc-url $RPC_URL \
  --private-key $PRIVATE_KEY \
  --constructor-args $USDC_ADDRESS $ORCHESTRATOR_ADDRESS \
  --json | tee deploy-new.json

export NEW_CONTRACT=$(cat deploy-new.json | jq -r '.deployedTo')
echo "New contract: $NEW_CONTRACT"
```

---

### Step 4 — Fund USDC reserves on new contract

The new contract must hold enough USDC to cover all imported liabilities
before `migrateBalances()` can succeed (enforced on-chain).

```bash
TOTAL=$(cat snapshot.json | jq '.totalLiabilities')

# Approve new contract to pull USDC from operator wallet
cast send $USDC_ADDRESS \
  "approve(address,uint256)" $NEW_CONTRACT $TOTAL \
  --private-key $PRIVATE_KEY \
  --rpc-url $RPC_URL

# Transfer USDC directly to new contract
cast send $USDC_ADDRESS \
  "transfer(address,uint256)" $NEW_CONTRACT $TOTAL \
  --private-key $PRIVATE_KEY \
  --rpc-url $RPC_URL
```

---

### Step 5 — Call `migrateBalances()` on new contract

Use the snapshot arrays from Step 2. The on-chain call verifies that the
contract holds enough USDC before setting `migrationComplete = true`.

```bash
# Build calldata from snapshot.json and call migrateBalances
# This is most easily done via a short ethers.js/cast script:

node migrate-balances.js \
  --contract $NEW_CONTRACT \
  --snapshot snapshot.json \
  --private-key $PRIVATE_KEY \
  --rpc $RPC_URL
```

> **Note:** `migrate-balances.js` does not exist yet. Build it when needed.
> It encodes the six arrays from `snapshot.json` and calls `migrateBalances()`.

Verify on-chain after the transaction confirms:

```bash
# Check migrationComplete flag
cast call $NEW_CONTRACT "migrationComplete()(bool)" --rpc-url $RPC_URL

# Spot-check a known user balance
cast call $NEW_CONTRACT \
  "userBalances(address)(uint256)" $KNOWN_USER_ADDRESS \
  --rpc-url $RPC_URL
```

---

### Step 6 — Initiate freeze on old contract

This starts the 3-day timelock. Users will see the `FreezeInitiated` event
with the new contract address on the block explorer.

```bash
cast send $OLD_CONTRACT \
  "initiateFreeze(address)" $NEW_CONTRACT \
  --private-key $PRIVATE_KEY \
  --rpc-url $RPC_URL
```

Confirm the timelock:

```bash
cast call $OLD_CONTRACT "freezeUnlocksAt()(uint256)" --rpc-url $RPC_URL
# Returns a Unix timestamp 3 days from now
```

---

### Step 6b — Unpause old contract during the 3-day window

Users must be able to withdraw from the old contract during the timelock.
If you paused in Step 1, unpause now:

```bash
cast send $OLD_CONTRACT "unpause()" \
  --private-key $PRIVATE_KEY \
  --rpc-url $RPC_URL
```

---

### Step 7 — Update orchestrator configuration

Update `appsettings.Payment.json` (or the environment variable) with the new
contract address. Redeploy or restart the orchestrator. From this point,
new settlements go to the new contract.

```json
{
  "Payment": {
    "EscrowContractAddress": "<NEW_CONTRACT>"
  }
}
```

Users who have not yet withdrawn from the old contract can still do so — the
old contract's `withdrawBalance()` and `nodeWithdraw()` remain open forever.

---

### Step 8 — Execute freeze after 3 days

After the timelock elapses, permanently freeze the old contract.

```bash
# Verify timelock has elapsed
UNLOCKS=$(cast call $OLD_CONTRACT "freezeUnlocksAt()(uint256)" --rpc-url $RPC_URL)
NOW=$(date +%s)
echo "Unlocks at: $UNLOCKS, Now: $NOW"

# Execute freeze
cast send $OLD_CONTRACT "executeFreeze()" \
  --private-key $PRIVATE_KEY \
  --rpc-url $RPC_URL
```

Verify:

```bash
cast call $OLD_CONTRACT "frozen()(bool)" --rpc-url $RPC_URL
# Returns: true
```

The old contract is now permanently frozen. New deposits are blocked.
All existing balances remain withdrawable indefinitely.

---

## Communicating the migration to users

Minimum required communication before calling `initiateFreeze()`:

1. **Blog post / announcement** — explain why migration is happening,
   what the new contract address is, and what users need to do (nothing,
   or re-deposit if they want to continue using the platform)
2. **Dashboard banner** — show a warning when users connect to the old
   contract address
3. **Email/Discord** — at least 3 days notice (matches the timelock)

The `FreezeInitiated` event on the block explorer serves as the on-chain
record of the migration announcement, including the new contract address.

---

## What happens to users who don't act

Nothing bad. Their balances on the old contract are permanently safe.
They can call `withdrawBalance()` at any time, years later, to retrieve
their USDC. The old contract is frozen (no new deposits), but withdrawals
are never blocked.

---

## Rollback

If `migrateBalances()` has been called but `initiateFreeze()` has not yet
been called on the old contract:

1. Unpause old contract if paused
2. Do not proceed with `initiateFreeze()`
3. The new contract has `migrationComplete = true` but has no authorized
   callers yet — it is effectively inert
4. Deploy another new contract if needed

There is no rollback once `executeFreeze()` has been called on the old
contract. However, the old contract's withdrawals remain open, so no
funds are lost.

---

## Security checklist before executing migration

- [ ] New contract code is audited or reviewed
- [ ] Snapshot total verified against old contract USDC balance
- [ ] `migrateBalances()` transaction confirmed on-chain
- [ ] Spot-checked at least 5 user balances on new contract match snapshot
- [ ] Orchestrator updated and tested on new contract before `initiateFreeze()`
- [ ] User communication published before `initiateFreeze()`
- [ ] 3-day window observed before `executeFreeze()`
- [ ] `frozen = true` verified on old contract after `executeFreeze()`
