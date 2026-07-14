"use client";

import * as signalR from "@microsoft/signalr";
import { useEffect, useRef, useCallback } from "react";
import { getAccessToken } from "@/lib/auth";

// Strip the trailing "/api" from the REST base URL to get the host origin,
// then append the hub path.
const API_BASE = process.env.NEXT_PUBLIC_API_URL ?? "http://localhost:5000/api";
const HUB_URL = API_BASE.replace(/\/api\/?$/, "") + "/hubs/migration";

export function useMigrationHub() {
  const connectionRef = useRef<signalR.HubConnection | null>(null);
  // Groups joined so far — SignalR group membership is per-connection-id and is
  // lost on every reconnect, so we must re-invoke Join* after `onreconnected`.
  const joinedProjectsRef = useRef<Set<string>>(new Set());
  const joinedScansRef = useRef<Set<string>>(new Set());
  const reconnectedHandlersRef = useRef<Set<() => void>>(new Set());

  // Disconnect when the component that owns the hook unmounts.
  useEffect(() => {
    return () => {
      const conn = connectionRef.current;
      connectionRef.current = null;
      // Don't stop a connection that is still negotiating — calling stop() during
      // negotiation throws "The connection was stopped during negotiation", which
      // is a no-op in practice but produces a noisy console error in React
      // Strict Mode (which double-invokes effects in development).
      if (conn && conn.state !== signalR.HubConnectionState.Connecting) {
        conn.stop().catch(() => {});
      }
    };
  }, []);

  /** Open a connection to the hub. Idempotent — safe to call multiple times. */
  const connect = useCallback(async () => {
    if (connectionRef.current) return;

    const token = await getAccessToken();
    if (!token) return; // Not authenticated — REST polling still works as fallback.

    const connection = new signalR.HubConnectionBuilder()
      // Async factory: reconnects always fetch a FRESH token (Entra tokens
      // expire ~hourly; the local dev token is returned as-is).
      .withUrl(HUB_URL, { accessTokenFactory: async () => (await getAccessToken()) ?? "" })
      .withAutomaticReconnect()
      .configureLogging(signalR.LogLevel.Warning)
      .build();

    // Re-join every group after an automatic reconnect (membership does not
    // survive the new connection id), then let subscribers catch up on events
    // missed while the socket was down.
    connection.onreconnected(() => {
      joinedProjectsRef.current.forEach((projectId) => {
        connection.invoke("JoinProject", projectId).catch(() => {});
      });
      joinedScansRef.current.forEach((scanId) => {
        connection.invoke("JoinScan", scanId).catch(() => {});
      });
      reconnectedHandlersRef.current.forEach((handler) => {
        try { handler(); } catch { /* subscriber error must not break others */ }
      });
    });

    connectionRef.current = connection;

    try {
      await connection.start();
      if (connectionRef.current !== connection) {
        connection.stop().catch(() => {});
      }
    } catch {
      if (connectionRef.current === connection) {
        connectionRef.current = null;
      }
    }
  }, []);

  const disconnect = useCallback(async () => {
    await connectionRef.current?.stop().catch(() => {});
    connectionRef.current = null;
  }, []);

  const joinProject = useCallback((projectId: string) => {
    joinedProjectsRef.current.add(projectId);
    connectionRef.current?.invoke("JoinProject", projectId).catch(() => {});
  }, []);

  const leaveProject = useCallback((projectId: string) => {
    joinedProjectsRef.current.delete(projectId);
    connectionRef.current?.invoke("LeaveProject", projectId).catch(() => {});
  }, []);

  const joinScan = useCallback((scanId: string) => {
    joinedScansRef.current.add(scanId);
    connectionRef.current?.invoke("JoinScan", scanId).catch(() => {});
  }, []);

  const leaveScan = useCallback((scanId: string) => {
    joinedScansRef.current.delete(scanId);
    connectionRef.current?.invoke("LeaveScan", scanId).catch(() => {});
  }, []);

  /** Register a callback fired after an automatic reconnect (groups are re-joined first). */
  const onReconnected = useCallback((handler: () => void) => {
    reconnectedHandlersRef.current.add(handler);
  }, []);

  const offReconnected = useCallback((handler: () => void) => {
    reconnectedHandlersRef.current.delete(handler);
  }, []);

  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  const on = useCallback((event: string, handler: (...args: any[]) => void) => {
    connectionRef.current?.on(event, handler);
  }, []);

  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  const off = useCallback((event: string, handler: (...args: any[]) => void) => {
    connectionRef.current?.off(event, handler);
  }, []);

  return { connect, disconnect, joinProject, leaveProject, joinScan, leaveScan, on, off, onReconnected, offReconnected };
}
