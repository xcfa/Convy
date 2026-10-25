import { useEffect, useRef, useState } from "react";
import { ChallengeWindow } from "./ChallengeWindow";
import { CLOSE_NOT_SET_UP, parseMessage, solverSocketUrl, type ChallengeOffer, type SolverMessage } from "./protocol";

/** A dropped solver socket is opened again after this long (ms), doubling up to `MAX_RETRY_MS`. */
const RETRY_MS = 2000;
const MAX_RETRY_MS = 60_000;
/** Without a service set up, the tab asks again this rarely (ms): captcha.url may be set meanwhile. */
const NOT_SET_UP_RETRY_MS = 5 * 60_000;

interface Shown {
  id: string;
  domain: string;
}

/**
 * The bot checks of this tab. While the UI is open, the tab is a solver at the service of checks for the plugins'
 * domains: an offer is a banner under the top bar ("Pass" / "Not now"), and "Pass" opens the window with the page of
 * the check.
 */
export function Challenges() {
  const solver = useRef<WebSocket | null>(null);
  const [offers, setOffers] = useState<ChallengeOffer[]>([]);
  const [shown, setShown] = useState<Shown | null>(null);

  useEffect(() => {
    let stopped = false;
    let attempts = 0;
    let retry: ReturnType<typeof setTimeout> | undefined;
    const connect = () => {
      const ws = new WebSocket(solverSocketUrl());
      solver.current = ws;
      ws.onmessage = (event: MessageEvent) => {
        const message = parseMessage(event.data) as SolverMessage | null;
        // The service took the tab: the backoff starts over.
        if (message?.type === "ready") attempts = 0;
        else if (message?.type === "offer") {
          const offer = { challengeId: message.challengeId, domain: message.domain, url: message.url, expiresAt: message.expiresAt };
          setOffers((list) => (list.some((o) => o.challengeId === offer.challengeId) ? list : [...list, offer]));
        } else if (message?.type === "closed") {
          setOffers((list) => list.filter((o) => o.challengeId !== message.challengeId));
        }
      };
      ws.onclose = (event: CloseEvent) => {
        if (solver.current === ws) solver.current = null;
        setOffers([]);
        if (stopped) return;
        const wait = event.code === CLOSE_NOT_SET_UP ? NOT_SET_UP_RETRY_MS : Math.min(MAX_RETRY_MS, RETRY_MS * 2 ** attempts++);
        retry = setTimeout(connect, wait);
      };
    };
    connect();
    return () => {
      stopped = true;
      clearTimeout(retry);
      solver.current?.close();
      solver.current = null;
    };
  }, []);

  const send = (message: object) => {
    const ws = solver.current;
    if (ws?.readyState === WebSocket.OPEN) ws.send(JSON.stringify(message));
  };
  const drop = (id: string) => setOffers((list) => list.filter((o) => o.challengeId !== id));

  return (
    <>
      {offers.length > 0 && (
        <section className="challenge-banners" aria-label="Bot checks">
          {offers.map((offer) => (
            <div key={offer.challengeId} className="banner warn challenge-banner">
              <div>
                <strong>{offer.domain}</strong> asks to pass a bot check
                <div className="small mono wrap-anywhere">{offer.url}</div>
              </div>
              <div className="challenge-actions">
                <button
                  className="button small"
                  onClick={() => {
                    send({ type: "accept", challengeId: offer.challengeId });
                    drop(offer.challengeId);
                    setShown({ id: offer.challengeId, domain: offer.domain });
                  }}
                >
                  Pass
                </button>
                <button
                  className="button small ghost"
                  onClick={() => {
                    send({ type: "decline", challengeId: offer.challengeId });
                    drop(offer.challengeId);
                  }}
                >
                  Not now
                </button>
              </div>
            </div>
          ))}
        </section>
      )}
      {shown && (
        <ChallengeWindow
          key={shown.id}
          id={shown.id}
          domain={shown.domain}
          onClose={(ended) => {
            // Taken from the banner and left before the end: the service hands it on to its next solver.
            if (!ended) send({ type: "decline", challengeId: shown.id });
            setShown(null);
          }}
        />
      )}
    </>
  );
}
