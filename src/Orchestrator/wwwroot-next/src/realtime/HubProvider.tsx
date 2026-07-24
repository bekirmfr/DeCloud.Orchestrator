import { createContext, useContext, useEffect, useRef, useState, type ReactNode } from "react";
import { useQueryClient } from "@tanstack/react-query";
import { HubConnectionBuilder, HubConnectionState, LogLevel, type HubConnection } from "@microsoft/signalr";
import { useAuth } from "../auth/AuthProvider";
import { tokenStore } from "../auth/tokenStore";

// ONE hub connection for the whole app (DESIGN §6.9). Owned here, started when the
// session is authenticated, torn down on sign-out. Components don't build their own
// connections — they consume this one and (un)subscribe to per-VM groups.
//
// Auth: the hub reads the JWT from the `access_token` QUERY param (browsers can't
// set Authorization headers on WebSockets — confirmed in Program.cs OnMessageReceived).
// The signalr client appends it automatically when given an accessTokenFactory.
//
// Reconnects: SignalR does NOT replay events missed while disconnected, and every
// broadcast in this system is change-gated — the server only sends when something
// actually differs. So a status, services or access-info transition that happens
// during a gap is gone for good, not delayed. On reconnect we therefore re-sync
// from REST rather than assume nothing moved.

interface HubContextValue {
    connection: HubConnection | null;
    ready: boolean;
}

const HubContext = createContext<HubContextValue>({ connection: null, ready: false });

export function useHub(): HubContextValue {
    return useContext(HubContext);
}

export function HubProvider({ children }: { children: ReactNode }) {
    const { session } = useAuth();
    const authed = session.kind === "authenticated" || session.kind === "uncertain";
    const connRef = useRef<HubConnection | null>(null);
    const [ready, setReady] = useState(false);
    const qc = useQueryClient();

    useEffect(() => {
        if (!authed) return;

        const connection = new HubConnectionBuilder()
            .withUrl("/hub/orchestrator", {
                // Pulls the current access token each (re)connect — survives refresh.
                accessTokenFactory: () => tokenStore.get() ?? "",
            })
            .withAutomaticReconnect()
            .configureLogging(LogLevel.Warning)
            .build();

        connRef.current = connection;
        let cancelled = false;

        connection
            .start()
            .then(() => { if (!cancelled) setReady(true); })
            .catch((e) => { console.warn("[hub] start failed:", e); });

        connection.onreconnected(() => {
            setReady(true);
            // Re-sync everything the hub feeds. Cheap (a handful of cached queries)
            // against the alternative: a cockpit silently stuck on a status the VM
            // left while the socket was down.
            qc.invalidateQueries({ queryKey: ["vm"] });
            qc.invalidateQueries({ queryKey: ["vms"] });
            qc.invalidateQueries({ queryKey: ["vm-metrics"] });
            qc.invalidateQueries({ queryKey: ["balance"] });
        });
        connection.onreconnecting(() => setReady(false));
        connection.onclose(() => setReady(false));

        return () => {
            cancelled = true;
            setReady(false);
            connRef.current = null;
            // stop() is fire-and-forget on unmount; guard state to avoid double-stop.
            if (connection.state !== HubConnectionState.Disconnected) {
                connection.stop().catch(() => { /* already tearing down */ });
            }
        };
    }, [authed, qc]);

    return (
        <HubContext.Provider value={{ connection: connRef.current, ready }}>
            {children}
        </HubContext.Provider>
    );
}