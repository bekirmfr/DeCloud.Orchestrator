import { useEffect } from "react";
import { useQueryClient } from "@tanstack/react-query";
import { useHub } from "./HubProvider";
import type { VmDetailResponse, VmMetrics } from "../features/vms/useVms";
import { normalizeStatus } from "../features/vms/vmStatus";

// Live updates for ONE VM's detail cockpit (DESIGN §6.9 scope: VM detail).
// On mount → SubscribeToVm(vmId) + register handlers that WRITE INTO the Query
// cache (["vm", vmId]) so useVm re-renders with fresh data — no parallel state.
// On unmount → UnsubscribeFromVm + remove exactly the handlers we added.
//
// Hub → client events (grounded from OrchestratorHub broadcasts):
//   VmStatusChanged     { VmId, Status, Message, Timestamp }
//   VmMetricsUpdated    { VmId, Metrics }
//   VmAccessInfoUpdated { VmId, AccessInfo }

interface VmStatusChanged { vmId: string; status: string | number; message?: string; timestamp?: string }
interface VmMetricsUpdated { vmId: string; metrics: VmMetrics }
interface VmAccessInfoUpdated { vmId: string; accessInfo: VmDetailResponse["vm"]["accessInfo"] }

export function useVmRealtime(vmId: string) {
  const { connection, ready } = useHub();
  const qc = useQueryClient();

  useEffect(() => {
    if (!connection || !ready || !vmId) return;
    const key = ["vm", vmId];

    // Patch the cached detail response in place (only when it's for THIS vm).
    const patchVm = (fn: (vm: VmDetailResponse["vm"]) => VmDetailResponse["vm"]) => {
      qc.setQueryData<VmDetailResponse>(key, (prev) =>
        prev ? { ...prev, vm: fn(prev.vm) } : prev
      );
    };

    const onStatus = (e: VmStatusChanged) => {
      if (e.vmId !== vmId) return;
      patchVm((vm) => ({ ...vm, status: normalizeStatus(e.status), statusMessage: e.message ?? vm.statusMessage }));
    };
    const onMetrics = (e: VmMetricsUpdated) => {
      if (e.vmId !== vmId) return;
      // Patch the SAME key useVmMetrics seeds from REST, so the panel shows the
      // last-known snapshot on load and then updates live from here.
      qc.setQueryData<VmMetrics>(["vm-metrics", vmId], e.metrics);
    };
    const onAccess = (e: VmAccessInfoUpdated) => {
      if (e.vmId !== vmId) return;
      patchVm((vm) => ({ ...vm, accessInfo: e.accessInfo ?? vm.accessInfo }));
    };

    connection.on("VmStatusChanged", onStatus);
    connection.on("VmMetricsUpdated", onMetrics);
    connection.on("VmAccessInfoUpdated", onAccess);
    connection.invoke("SubscribeToVm", vmId).catch((err) =>
      console.warn("[hub] SubscribeToVm failed:", err)
    );

    return () => {
      connection.off("VmStatusChanged", onStatus);
      connection.off("VmMetricsUpdated", onMetrics);
      connection.off("VmAccessInfoUpdated", onAccess);
      // Best-effort unsubscribe; connection may already be gone on teardown.
      connection.invoke("UnsubscribeFromVm", vmId).catch(() => { /* connection closing */ });
    };
  }, [connection, ready, vmId, qc]);
}
