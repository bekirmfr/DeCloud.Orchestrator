import { useEffect } from "react";
import { useQueryClient } from "@tanstack/react-query";
import { useHub } from "./HubProvider";
import { normalizeStatus } from "../features/vms/vmStatus";
import type { PagedResult, VmSummary } from "../features/vms/useVms";

// Live status for EVERY VM a user owns — the dashboard's running-workloads list
// (DESIGN §2: one of only two places real-time earns its place).
//
// The hub has exposed SubscribeToUser since it was written, but until the
// user-group broadcast landed nothing ever published to `user:{id}`. This is
// its first consumer.
//
// `userId` is the WALLET ADDRESS: VirtualMachine.OwnerId is the wallet
// (confirmed on a live record, and the JWT `sub` is the same value), and the
// server broadcasts to `user:{vm.OwnerId}`.

interface VmStatusChanged {
  vmId: string;
  status: string | number;
  message?: string;
}

export function useUserRealtime(userId: string) {
  const { connection, ready } = useHub();
  const qc = useQueryClient();

  useEffect(() => {
    if (!connection || !ready || !userId) return;

    const onStatus = (e: VmStatusChanged) => {
      // Patch every cached VM list page that contains this VM. setQueriesData
      // matches by key PREFIX, so ["vms", 1], ["vms", 2] … are all covered
      // without the dashboard needing to know which page holds it.
      qc.setQueriesData<PagedResult<VmSummary>>({ queryKey: ["vms"] }, (prev) => {
        if (!prev) return prev;
        // `some` first so a page that doesn't hold this VM keeps its identity
        // and doesn't re-render; map avoids indexed access, which under
        // noUncheckedIndexedAccess would widen every field to optional.
        if (!prev.items.some((v) => v.id === e.vmId)) return prev;
        return {
          ...prev,
          items: prev.items.map((v) =>
            v.id === e.vmId ? { ...v, status: normalizeStatus(e.status) } : v
          ),
        };
      });

      // A status change also moves the money: a VM starting or stopping changes
      // the burn rate, so the runway figure is now stale.
      qc.invalidateQueries({ queryKey: ["balance"] });
    };

    connection.on("VmStatusChanged", onStatus);
    connection.invoke("SubscribeToUser", userId).catch((err) =>
      console.warn("[hub] SubscribeToUser failed:", err)
    );

    return () => {
      connection.off("VmStatusChanged", onStatus);
    };
  }, [connection, ready, userId, qc]);
}