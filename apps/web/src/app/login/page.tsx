"use client";

import { useState, useEffect, FormEvent } from "react";
import { useRouter } from "next/navigation";
import { authApi } from "@/lib/api";
import {
  setToken,
  ensureAuthenticated,
  getAuthConfig,
  msalLogin,
  type AuthMode,
} from "@/lib/auth";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import {
  Card,
  CardHeader,
  CardTitle,
  CardDescription,
  CardContent,
} from "@/components/ui/card";

export default function LoginPage() {
  const router = useRouter();
  const [mode, setMode] = useState<AuthMode | null>(null); // null = loading config
  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);
  // Set after a successful local login that still uses the seeded default
  // password — swaps the card to a change-password form.
  const [changingPassword, setChangingPassword] = useState(false);
  const [newPassword, setNewPassword] = useState("");
  const [confirmPassword, setConfirmPassword] = useState("");

  // Resolve the auth mode, and skip to the dashboard if already signed in.
  useEffect(() => {
    let cancelled = false;
    (async () => {
      if (await ensureAuthenticated()) {
        router.replace("/");
        return;
      }
      const cfg = await getAuthConfig();
      if (!cancelled) setMode(cfg.mode);
    })();
    return () => {
      cancelled = true;
    };
  }, [router]);

  async function handleSubmit(e: FormEvent) {
    e.preventDefault();
    setError(null);
    setLoading(true);
    try {
      const { token, expiresAt, mustChangePassword } = await authApi.login(username, password);
      setToken(token, expiresAt);
      if (mustChangePassword) {
        // Signed in with the seeded default password — offer a change now.
        setChangingPassword(true);
        return;
      }
      router.replace("/");
    } catch (err) {
      setError(err instanceof Error ? err.message : "Login failed. Check your credentials and try again.");
    } finally {
      setLoading(false);
    }
  }

  async function handleChangePassword(e: FormEvent) {
    e.preventDefault();
    setError(null);
    if (newPassword !== confirmPassword) {
      setError("Passwords do not match.");
      return;
    }
    setLoading(true);
    try {
      await authApi.changePassword(password, newPassword);
      router.replace("/");
    } catch (err) {
      setError(err instanceof Error ? err.message : "Password change failed.");
    } finally {
      setLoading(false);
    }
  }

  async function handleMicrosoftSignIn() {
    setError(null);
    setLoading(true);
    try {
      await msalLogin();
      router.replace("/");
    } catch (err) {
      setError(
        err instanceof Error && err.message
          ? err.message
          : "Microsoft sign-in was cancelled or failed. Try again."
      );
    } finally {
      setLoading(false);
    }
  }

  return (
    <div className="flex min-h-screen items-center justify-center bg-background p-4">
      <Card className="w-full max-w-sm">
        <CardHeader className="space-y-1">
          <CardTitle className="text-2xl">M365 Migration Platform</CardTitle>
          <CardDescription>
            {mode === "entraId"
              ? "Sign in with your Microsoft work account"
              : "Sign in to your administrator account"}
          </CardDescription>
        </CardHeader>
        <CardContent>
          {mode === null && (
            <p className="text-sm text-muted-foreground">Loading sign-in options…</p>
          )}

          {mode === "entraId" && (
            <div className="space-y-4">
              <Button
                type="button"
                className="w-full"
                onClick={handleMicrosoftSignIn}
                disabled={loading}
              >
                {/* Microsoft logo (four squares) */}
                <svg className="mr-2 h-4 w-4" viewBox="0 0 21 21" aria-hidden="true">
                  <rect x="1" y="1" width="9" height="9" fill="#f25022" />
                  <rect x="11" y="1" width="9" height="9" fill="#7fba00" />
                  <rect x="1" y="11" width="9" height="9" fill="#00a4ef" />
                  <rect x="11" y="11" width="9" height="9" fill="#ffb900" />
                </svg>
                {loading ? "Signing in…" : "Sign in with Microsoft"}
              </Button>
              {error && (
                <p className="text-sm text-destructive" role="alert">
                  {error}
                </p>
              )}
            </div>
          )}

          {mode === "none" && (
            <p className="text-sm text-muted-foreground" role="alert">
              Authentication is not configured on the API. Set the{" "}
              <code className="rounded bg-muted px-1">AzureAd</code> section
              (TenantId, ClientId) in <code className="rounded bg-muted px-1">appsettings.json</code>{" "}
              for Microsoft Entra ID sign-in, or enable{" "}
              <code className="rounded bg-muted px-1">Platform:DevMode</code>{" "}
              for local development login.
            </p>
          )}

          {mode === "local" && changingPassword && (
            <form onSubmit={handleChangePassword} className="space-y-4">
              <p className="text-sm text-muted-foreground">
                You signed in with the default password. Set a new one now
                (minimum 12 characters), or skip and change it later.
              </p>
              <div className="space-y-2">
                <Label htmlFor="new-password">New password</Label>
                <Input
                  id="new-password"
                  type="password"
                  autoComplete="new-password"
                  value={newPassword}
                  onChange={(e) => setNewPassword(e.target.value)}
                  minLength={12}
                  required
                  disabled={loading}
                />
              </div>
              <div className="space-y-2">
                <Label htmlFor="confirm-password">Confirm new password</Label>
                <Input
                  id="confirm-password"
                  type="password"
                  autoComplete="new-password"
                  value={confirmPassword}
                  onChange={(e) => setConfirmPassword(e.target.value)}
                  minLength={12}
                  required
                  disabled={loading}
                />
              </div>
              {error && (
                <p className="text-sm text-destructive" role="alert">
                  {error}
                </p>
              )}
              <Button type="submit" className="w-full" disabled={loading}>
                {loading ? "Saving..." : "Change password"}
              </Button>
              <Button
                type="button"
                variant="ghost"
                className="w-full"
                disabled={loading}
                onClick={() => router.replace("/")}
              >
                Skip for now
              </Button>
            </form>
          )}

          {mode === "local" && !changingPassword && (
            <form onSubmit={handleSubmit} className="space-y-4">
              <div className="space-y-2">
                <Label htmlFor="username">Username</Label>
                <Input
                  id="username"
                  type="text"
                  autoComplete="username"
                  placeholder="admin"
                  value={username}
                  onChange={(e) => setUsername(e.target.value)}
                  required
                  disabled={loading}
                />
              </div>
              <div className="space-y-2">
                <Label htmlFor="password">Password</Label>
                <Input
                  id="password"
                  type="password"
                  autoComplete="current-password"
                  value={password}
                  onChange={(e) => setPassword(e.target.value)}
                  required
                  disabled={loading}
                />
              </div>
              {error && (
                <p className="text-sm text-destructive" role="alert">
                  {error}
                </p>
              )}
              <Button type="submit" className="w-full" disabled={loading}>
                {loading ? "Signing in..." : "Sign in"}
              </Button>
            </form>
          )}
        </CardContent>
      </Card>
    </div>
  );
}
