"use client";

import { useEffect, useState } from "react";
import { useRouter } from "next/navigation";
import { ensureAuthenticated } from "@/lib/auth";

interface AuthGuardProps {
  children: React.ReactNode;
}

export function AuthGuard({ children }: AuthGuardProps) {
  const [checked, setChecked] = useState(false);
  const router = useRouter();

  useEffect(() => {
    let cancelled = false;
    // Mode-aware: local dev token or an MSAL account (Entra ID mode).
    ensureAuthenticated().then((ok) => {
      if (cancelled) return;
      if (!ok) router.replace("/login");
      else setChecked(true);
    });
    return () => {
      cancelled = true;
    };
  }, [router]);

  if (!checked) return null;

  return <>{children}</>;
}
