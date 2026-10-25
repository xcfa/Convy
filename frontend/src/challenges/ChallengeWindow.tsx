import { useEffect, useEffectEvent, useRef, useState, type KeyboardEvent, type PointerEvent } from "react";
import {
  buttonName,
  CLOSE_NOT_FOUND,
  CLOSE_NOT_SET_UP,
  CLOSE_UNAVAILABLE,
  parseMessage,
  screenSocketUrl,
  secondsLeft,
  toPage,
  type InputEvent,
  type ScreenHello,
  type ScreenMessage,
} from "./protocol";

/** A dropped socket is opened again after this long (ms), at most `MAX_RECONNECTS` times in a row. */
const RECONNECT_MS = 2000;
const MAX_RECONNECTS = 5;
/** "Check passed" stays this long (ms) before the window closes itself. */
const SOLVED_CLOSE_MS = 2000;
/** Pointer moves go up at most this often (ms). */
const MOVE_EVERY_MS = 40;
/** Keys that are no key of their own (a modifier alone, a key inside an IME's or a phone keyboard's composition). */
const SILENT_KEYS = new Set(["Unidentified", "Process", "Dead", "Shift", "Control", "Alt", "AltGraph", "Meta", "CapsLock"]);

type Phase = "connecting" | "waiting" | "live" | "lost" | "ended";

function sendOn(ws: WebSocket | null, message: object) {
  if (ws?.readyState === WebSocket.OPEN) ws.send(JSON.stringify(message));
}

interface WindowProps {
  id: string;
  domain: string;
  /** `ended` — the check is over (passed or not), so nobody else needs to take it. */
  onClose: (ended: boolean) => void;
}

/**
 * The window of a bot check: the page of the service's browser on a canvas, frames down the screen socket, clicks,
 * the wheel and keys up. On a phone the picture is scaled and a tap is a click. Passed — "Check passed", and the
 * window closes after two seconds.
 */
export function ChallengeWindow({ id, domain, onClose }: WindowProps) {
  const canvas = useRef<HTMLCanvasElement>(null);
  const keys = useRef<HTMLInputElement>(null);
  const socket = useRef<WebSocket | null>(null);
  const lastMove = useRef(0);
  const closeSolved = useEffectEvent(() => onClose(true));
  // The service said the check is over (passed or failed); the window's own failures leave it to others.
  const over = useRef(false);
  const close = useEffectEvent(() => onClose(over.current));
  const [hello, setHello] = useState<ScreenHello | null>(null);
  const [control, setControl] = useState(false);
  const [phase, setPhase] = useState<Phase>("connecting");
  const [end, setEnd] = useState<{ text: string; solved: boolean } | null>(null);
  const [now, setNow] = useState(() => Date.now());

  useEffect(() => {
    let finished = false;
    let failures = 0;
    let retry: ReturnType<typeof setTimeout> | undefined;
    let closing: ReturnType<typeof setTimeout> | undefined;

    const finish = (text: string, solved: boolean) => {
      finished = true;
      setEnd({ text, solved });
      setPhase("ended");
      if (solved) closing = setTimeout(() => closeSolved(), SOLVED_CLOSE_MS);
    };

    // Frames decode in parallel: one that finishes after a newer frame is dropped; a broken one is skipped.
    let decoded = 0;
    let drawn = 0;
    const draw = async (frame: Blob) => {
      const target = canvas.current;
      const context = target?.getContext("2d");
      if (!target || !context || typeof createImageBitmap !== "function") return;
      const order = ++decoded;
      const bitmap = await createImageBitmap(frame).catch(() => null);
      if (!bitmap) return;
      if (order > drawn) {
        drawn = order;
        context.drawImage(bitmap, 0, 0, target.width, target.height);
      }
      bitmap.close();
    };

    const connect = () => {
      const ws = new WebSocket(screenSocketUrl(id));
      ws.binaryType = "blob";
      socket.current = ws;
      ws.onmessage = (event: MessageEvent) => {
        if (event.data instanceof Blob) {
          void draw(event.data);
          return;
        }
        const message = parseMessage(event.data) as ScreenMessage | null;
        switch (message?.type) {
          case "waiting":
            setPhase("waiting");
            break;
          case "hello":
            failures = 0;
            setHello(message);
            setControl(message.control);
            setPhase("live");
            break;
          case "control":
            setControl(message.control);
            break;
          case "closed":
            // pending: the page closed under this window while the check goes on; the socket's close reconnects.
            if (message.state === "pending") break;
            over.current = true;
            finish(
              message.state === "solved" ? "Check passed" : message.message ? `Check failed: ${message.message}` : "Check failed",
              message.state === "solved",
            );
            break;
        }
      };
      ws.onclose = (event: CloseEvent) => {
        if (socket.current === ws) socket.current = null;
        if (finished) return;
        const known =
          event.code === CLOSE_NOT_FOUND
            ? "The check is already over"
            : event.code === CLOSE_NOT_SET_UP
              ? "No service of checks is set up (captcha.url, captcha.key)"
              : event.code === CLOSE_UNAVAILABLE || failures >= MAX_RECONNECTS
                ? "The service of checks is unavailable"
                : null;
        if (known) {
          finish(known, false);
          return;
        }
        failures++;
        setPhase("lost");
        retry = setTimeout(connect, RECONNECT_MS);
      };
    };

    connect();
    return () => {
      finished = true;
      clearTimeout(retry);
      clearTimeout(closing);
      socket.current?.close();
      socket.current = null;
    };
  }, [id]);

  // The countdown to the check's end.
  useEffect(() => {
    if (phase !== "live") return;
    const timer = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(timer);
  }, [phase]);

  // Escape closes the window, as the other dialogs' "Close".
  useEffect(() => {
    const onKey = (event: globalThis.KeyboardEvent) => {
      if (event.key === "Escape") close();
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, []);

  const send = (message: object) => sendOn(socket.current, message);
  const live = phase === "live" && hello !== null;
  const input = (event: InputEvent) => {
    if (live && control) send({ type: "input", event });
  };
  const point = (event: PointerEvent<HTMLCanvasElement>) =>
    toPage(event.clientX, event.clientY, event.currentTarget.getBoundingClientRect(), hello ?? { width: 1, height: 1 });

  // The wheel scrolls the page in the service's browser, not this one: a listener that may prevent it.
  useEffect(() => {
    const target = canvas.current;
    if (!target || !live || !control) return;
    const onWheel = (event: WheelEvent) => {
      event.preventDefault();
      sendOn(socket.current, { type: "input", event: { type: "wheel", deltaX: event.deltaX, deltaY: event.deltaY } });
    };
    target.addEventListener("wheel", onWheel, { passive: false });
    return () => target.removeEventListener("wheel", onWheel);
  }, [live, control]);

  const onKeyDown = (event: KeyboardEvent<HTMLInputElement>) => {
    // Escape is left to the window; printable characters come as text (the phone's keyboard too). A shortcut has no
    // counterpart there and stays with the field: a paste comes up as its text, AltGr's characters too.
    if (event.key === "Escape" || event.nativeEvent.isComposing || SILENT_KEYS.has(event.key) || event.ctrlKey || event.metaKey) return;
    if (event.key.length > 1) {
      event.preventDefault();
      event.stopPropagation();
      input({ type: "key", key: event.key });
    }
  };
  // The field's text goes up once it is final: an IME's or a phone keyboard's word when the composition ends.
  const flush = (field: HTMLInputElement) => {
    if (!field.value) return;
    input({ type: "text", text: field.value });
    field.value = "";
  };

  const left = hello ? secondsLeft(hello.expiresAt, now) : null;
  const status =
    phase === "ended"
      ? null
      : phase === "connecting"
        ? "Connecting…"
        : phase === "waiting"
          ? "The check waits for its turn…"
          : phase === "lost"
            ? "Connection lost. Reconnecting…"
            : left === 0
              ? "Time is up"
              : left !== null
                ? `${Math.floor(left / 60)}:${String(left % 60).padStart(2, "0")} left`
                : null;

  return (
    <div className="dialog-backdrop">
      <div className="dialog card challenge-window" role="dialog" aria-label={`Bot check of ${hello?.domain ?? domain}`}>
        <div className="card-head">
          <h2>Bot check of {hello?.domain ?? domain}</h2>
          <button className="button ghost" onClick={() => onClose(over.current)}>
            Close
          </button>
        </div>
        {hello && <div className="small mono muted challenge-url">{hello.url}</div>}
        {end ? (
          <div className={end.solved ? "banner ok" : "banner bad"} role="status">
            {end.text}
          </div>
        ) : (
          <p className="small muted challenge-hint">
            Pass the check on the page as on any site: clicks and keys go to the service's browser.
          </p>
        )}
        <div className="challenge-screen" hidden={end !== null}>
          <canvas
            ref={canvas}
            className="challenge-canvas"
            width={hello?.width ?? 1280}
            height={hello?.height ?? 800}
            aria-label="Page of the check"
            tabIndex={-1}
            hidden={!hello || end !== null}
            onContextMenu={(event) => event.preventDefault()}
            // The click would focus the canvas after the keys' field took the focus.
            onMouseDown={(event) => event.preventDefault()}
            onPointerDown={(event) => {
              // A drag that leaves the picture (a slider) still ends with its "up".
              try {
                event.currentTarget.setPointerCapture(event.pointerId);
              } catch {
                // The pointer is gone.
              }
              keys.current?.focus({ preventScroll: true });
              input({ type: "down", button: buttonName(event.button), ...point(event) });
            }}
            onPointerUp={(event) => input({ type: "up", button: buttonName(event.button), ...point(event) })}
            onPointerMove={(event) => {
              const at = performance.now();
              if (at - lastMove.current < MOVE_EVERY_MS) return;
              lastMove.current = at;
              input({ type: "move", ...point(event) });
            }}
          />
          <input
            ref={keys}
            className="challenge-keys"
            aria-label="Keyboard input for the page of the check"
            autoComplete="off"
            autoCapitalize="off"
            onKeyDown={onKeyDown}
            onInput={(event) => {
              if (!(event.nativeEvent as globalThis.InputEvent).isComposing) flush(event.currentTarget);
            }}
            onCompositionEnd={(event) => flush(event.currentTarget)}
          />
        </div>
        {phase !== "ended" && (
          <div className="challenge-footer">
            {status && (
              <span className="small muted challenge-status" role="status">
                {status}
              </span>
            )}
            {live && !control && <span className="small muted">Another window controls the page now</span>}
            {live && !control && (
              <button className="button ghost" onClick={() => send({ type: "take" })}>
                Take control
              </button>
            )}
            {live && control && hello.needsDone && (
              <button className="button" onClick={() => send({ type: "done" })}>
                Done
              </button>
            )}
          </div>
        )}
      </div>
    </div>
  );
}
