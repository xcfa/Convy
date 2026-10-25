// The messages of the service of checks (captcha-solver) as Convy relays them at /api/ui/captcha: the tab's solver
// socket (`ready`, `offer`, `closed` down; `accept`, `decline` up) and the page of one check (`waiting`, `hello`,
// `control`, `closed` and JPEG frames down; `take`, `input`, `done` up). Coordinates of `input` are the page's
// (`width`×`height`).

/** A check the service offers this tab: a person passes it in the window. */
export interface ChallengeOffer {
  challengeId: string;
  domain: string;
  url: string;
  expiresAt: string;
}

export type ChallengeEnd = "pending" | "solved" | "failed";

export type SolverMessage =
  | { type: "ready"; id: string; priority: number }
  | ({ type: "offer" } & ChallengeOffer)
  | { type: "closed"; challengeId: string; state: ChallengeEnd; message?: string | null };

export interface ScreenHello {
  challengeId: string;
  domain: string;
  url: string;
  expiresAt: string;
  width: number;
  height: number;
  /** A site's captcha without conditions: only the person's "Done" ends it. */
  needsDone: boolean;
  /** This window's clicks and keys count. */
  control: boolean;
}

export type ScreenMessage =
  | { type: "waiting" }
  | ({ type: "hello" } & ScreenHello)
  | { type: "control"; control: boolean }
  | { type: "closed"; state: ChallengeEnd; message?: string | null };

export type InputEvent =
  | { type: "move"; x: number; y: number }
  | { type: "down" | "up"; x: number; y: number; button: "left" | "middle" | "right" }
  | { type: "wheel"; deltaX: number; deltaY: number }
  | { type: "key"; key: string }
  | { type: "text"; text: string };

/** How Convy closes a socket when the service is not there (CaptchaWindowRelay). */
export const CLOSE_NOT_FOUND = 4404;
export const CLOSE_NOT_SET_UP = 4409;
export const CLOSE_UNAVAILABLE = 4502;

function socketUrl(path: string): string {
  const scheme = window.location.protocol === "https:" ? "wss" : "ws";
  return `${scheme}://${window.location.host}/api/ui/captcha${path}`;
}

export const solverSocketUrl = () => socketUrl("/solvers/connect");
export const screenSocketUrl = (id: string) => socketUrl(`/challenges/${encodeURIComponent(id)}/screen`);

/** A text message as an object with a `type` (the caller knows which socket it came from); anything else — null. */
export function parseMessage(data: unknown): { type: string } | null {
  if (typeof data !== "string") return null;
  try {
    const value: unknown = JSON.parse(data);
    return typeof value === "object" && value !== null && typeof (value as { type?: unknown }).type === "string"
      ? (value as { type: string })
      : null;
  } catch {
    return null;
  }
}

/** A pointer position on the shown picture → the page's coordinates. */
export function toPage(
  clientX: number,
  clientY: number,
  box: { left: number; top: number; width: number; height: number },
  page: { width: number; height: number },
): { x: number; y: number } {
  return {
    x: Math.round(((clientX - box.left) * page.width) / Math.max(1, box.width)),
    y: Math.round(((clientY - box.top) * page.height) / Math.max(1, box.height)),
  };
}

export function buttonName(button: number): "left" | "middle" | "right" {
  return button === 2 ? "right" : button === 1 ? "middle" : "left";
}

/** Seconds until `expiresAt` (never below zero). */
export function secondsLeft(expiresAt: string, now: number): number {
  return Math.max(0, Math.round((Date.parse(expiresAt) - now) / 1000));
}
