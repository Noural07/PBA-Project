import { useCallback, useEffect, useMemo, useState } from "react";
import type {
  AlertClassification,
  AlertClassificationsResponse,
  ConsolidatedAlert,
} from "./types";

/**
 * Single-page-komponenten der subscriber til AlertingService' SSE-stream
 * via den native EventSource-API. Bevidst implementeret uden router og uden
 * state-management-bibliotek for at holde frontend'en så transparent som
 * muligt – jf. designkravet i Phase 4.
 *
 * Fase C-udvidelser: viser AI-severity som farvebadge og en konkret
 * recommendedAction-kolonne, så hele AI-laget er synligt for operatøren
 * uden at skulle tilgå Loki eller Postgres.
 *
 * Fase 4-CQRS-udvidelse: hver række er klikbar og folder ud til at vise
 * den fulde liste af AI-klassifikationer for batchen, hentet on-demand fra
 * `GET /alerts/{correlationId}/classifications`. Hovedrækken viser kun den
 * klassifikation der matcher TopReason; drill-downen viser audit-sporet.
 */
export default function App() {
  const [alerts, setAlerts] = useState<ConsolidatedAlert[]>([]);
  const [status, setStatus] = useState<"connecting" | "open" | "error">(
    "connecting",
  );

  const baseUrl = useMemo(
    () => (import.meta.env.VITE_ALERTING_BASE_URL ?? "").replace(/\/$/, ""),
    [],
  );

  useEffect(() => {
    if (!baseUrl) {
      console.error(
        "VITE_ALERTING_BASE_URL er ikke sat – frontend'en kan ikke koble til SSE-streamen.",
      );
      setStatus("error");
      return;
    }

    const url = `${baseUrl}/alerts/stream`;
    const source = new EventSource(url);

    const ingest = (event: MessageEvent<string>) => {
      try {
        const parsed = JSON.parse(event.data) as ConsolidatedAlert;
        setAlerts((current) => mergeAlert(current, parsed));
      } catch (error) {
        console.warn("Ugyldigt SSE-payload modtaget", error);
      }
    };

    source.addEventListener("snapshot", ingest as EventListener);
    source.addEventListener("alert", ingest as EventListener);
    source.onopen = () => setStatus("open");
    source.onerror = () => setStatus("error");

    return () => {
      source.close();
    };
  }, [baseUrl]);

  return (
    <main>
      <header>
        <h1>PBA – AlertFeed</h1>
        <p className="subtitle">
          Live-stream af konsoliderede alarmer fra AlertingService.
        </p>
        <span className={`status status--${status}`} aria-live="polite">
          {status === "connecting" && "Forbinder…"}
          {status === "open" && "Forbundet"}
          {status === "error" && "Forbindelsesfejl"}
        </span>
      </header>

      {alerts.length === 0 ? (
        <p className="empty">
          Ingen alarmer endnu. Triggér en kritisk hændelse for at se den her.
        </p>
      ) : (
        <table>
          <thead>
            <tr>
              <th aria-label="Udfold" className="col-expand" />
              <th>Tidspunkt</th>
              <th>Severity</th>
              <th>Regel</th>
              <th>Top årsag</th>
              <th>AI kategori</th>
              <th>AI standardiseret</th>
              <th>AI severity</th>
              <th>AI confidence</th>
              <th>Anbefalet handling</th>
            </tr>
          </thead>
          <tbody>
            {alerts.map((alert) => (
              <AlertRow key={alert.correlationId} alert={alert} baseUrl={baseUrl} />
            ))}
          </tbody>
        </table>
      )}
    </main>
  );
}

/**
 * Én række i AlertFeed med drill-down for at vise alle AI-klassifikationer
 * der hører til batchen. Klassifikationerne hentes lazy: ingen netværks-
 * trafik før operatøren rent faktisk åbner rækken første gang.
 */
function AlertRow({
  alert,
  baseUrl,
}: {
  alert: ConsolidatedAlert;
  baseUrl: string;
}) {
  type LoadState =
    | { kind: "idle" }
    | { kind: "loading" }
    | { kind: "loaded"; rows: AlertClassification[] }
    | { kind: "error"; message: string };

  const [expanded, setExpanded] = useState(false);
  const [load, setLoad] = useState<LoadState>({ kind: "idle" });

  const fetchClassifications = useCallback(async () => {
    if (!baseUrl) return;
    setLoad({ kind: "loading" });
    try {
      const response = await fetch(
        `${baseUrl}/alerts/${alert.correlationId}/classifications`,
        { headers: { Accept: "application/json" } },
      );
      if (!response.ok) {
        throw new Error(`HTTP ${response.status}`);
      }
      const payload = (await response.json()) as AlertClassificationsResponse;
      setLoad({ kind: "loaded", rows: payload.classifications });
    } catch (error) {
      setLoad({
        kind: "error",
        message:
          error instanceof Error
            ? error.message
            : "Ukendt fejl ved hentning af klassifikationer.",
      });
    }
  }, [alert.correlationId, baseUrl]);

  const toggle = useCallback(() => {
    setExpanded((current) => {
      const next = !current;
      if (next && load.kind === "idle") {
        void fetchClassifications();
      }
      return next;
    });
  }, [fetchClassifications, load.kind]);

  // Total antal kolonner = 10 (1 udfold + 9 data). Detail-rækken spænder
  // hele bredden så drill-down-tabellen kan flugte under hovedrækken.
  const colSpan = 10;

  return (
    <>
      <tr
        className={`severity-${alert.severity.toLowerCase()} alert-row${expanded ? " alert-row--expanded" : ""}`}
        onClick={toggle}
        role="button"
        aria-expanded={expanded}
        aria-label={`Udfold klassifikationer for ${alert.topReason ?? "alarm"}`}
        tabIndex={0}
        onKeyDown={(e) => {
          if (e.key === "Enter" || e.key === " ") {
            e.preventDefault();
            toggle();
          }
        }}
      >
        <td className="col-expand">
          <span className={`chevron${expanded ? " chevron--open" : ""}`} aria-hidden>
            ▸
          </span>
        </td>
        <td>{formatTimestamp(alert.timestamp)}</td>
        <td>{alert.severity}</td>
        <td>{alert.rule}</td>
        <td>{alert.topReason ?? "-"}</td>
        <td>{formatCategory(alert.aiCategory, alert.aiSubcategory)}</td>
        <td>{alert.aiStandardizedReason ?? "-"}</td>
        <td className={aiSeverityClass(alert.aiSeverity)}>
          {alert.aiSeverity ?? "-"}
        </td>
        <td>{formatConfidence(alert.aiConfidence, alert.aiIsFallback)}</td>
        <td>{alert.aiRecommendedAction ?? "-"}</td>
      </tr>
      {expanded && (
        <tr className="drill-down">
          <td colSpan={colSpan}>
            <ClassificationDetail
              load={load}
              topReason={alert.topReason ?? null}
            />
          </td>
        </tr>
      )}
    </>
  );
}

function ClassificationDetail({
  load,
  topReason,
}: {
  load:
    | { kind: "idle" }
    | { kind: "loading" }
    | { kind: "loaded"; rows: AlertClassification[] }
    | { kind: "error"; message: string };
  topReason: string | null;
}) {
  if (load.kind === "idle" || load.kind === "loading") {
    return <p className="drill-down__status">Henter klassifikationer…</p>;
  }
  if (load.kind === "error") {
    return (
      <p className="drill-down__status drill-down__status--error">
        Kunne ikke hente audit-klassifikationer: {load.message}
      </p>
    );
  }
  if (load.rows.length === 0) {
    return (
      <p className="drill-down__status">
        Ingen AI-klassifikationer registreret for denne alarm.
      </p>
    );
  }

  return (
    <div className="drill-down__inner">
      <p className="drill-down__intro">
        Alle {load.rows.length} AI-klassifikationer for denne batch. Hovedrækken
        viser kun den der matcher TopReason
        {topReason ? <> (<strong>{topReason}</strong>)</> : null}; resten er
        bevaret som audit-spor i <code>classified_stop_reasons</code>.
      </p>
      <table className="drill-down__table">
        <thead>
          <tr>
            <th>Tidspunkt</th>
            <th>Original reason</th>
            <th>AI kategori</th>
            <th>Standardiseret</th>
            <th>Severity</th>
            <th>Confidence</th>
            <th>Anbefaling</th>
          </tr>
        </thead>
        <tbody>
          {load.rows.map((row) => {
            const isWinner =
              topReason !== null &&
              row.originalReason === topReason;
            return (
              <tr
                key={row.eventId}
                className={isWinner ? "drill-down__row--winner" : undefined}
              >
                <td>{formatTimestamp(row.occurredAt)}</td>
                <td>
                  {row.originalReason}
                  {isWinner && (
                    <span className="badge badge--match" title="Vinder visning – matcher TopReason">
                      match
                    </span>
                  )}
                </td>
                <td>{formatCategory(row.category, row.subcategory)}</td>
                <td>{row.standardizedReason}</td>
                <td className={aiSeverityClass(row.severity)}>{row.severity}</td>
                <td>{formatConfidence(row.confidence, row.isFallback)}</td>
                <td>{row.recommendedAction}</td>
              </tr>
            );
          })}
        </tbody>
      </table>
    </div>
  );
}

/**
 * Indfletter en netop modtaget alarm i listen. Hvis korrelations-ID allerede
 * findes (eksempelvis fordi snapshot'et indeholdt en placeholder uden AI-felter),
 * erstattes den eksisterende række in-place. Listen holdes på maks. 50
 * elementer i omvendt kronologisk rækkefølge.
 */
function mergeAlert(
  current: ConsolidatedAlert[],
  incoming: ConsolidatedAlert,
): ConsolidatedAlert[] {
  const filtered = current.filter(
    (alert) => alert.correlationId !== incoming.correlationId,
  );
  const next = [incoming, ...filtered];
  return next.slice(0, 50);
}

function formatTimestamp(iso: string): string {
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) return iso;
  return date.toLocaleString("da-DK", {
    hour12: false,
  });
}

/**
 * Sammensætter Trendlogs grov-kategori og Geminis danske underkategori i
 * én visning. Hvis underkategorien mangler eller er identisk med
 * grov-kategorien, vises kun grov-kategorien.
 */
function formatCategory(
  category: string | null | undefined,
  subcategory: string | null | undefined,
): string {
  if (!category) return "-";
  if (!subcategory || subcategory === category) return category;
  return `${category} / ${subcategory}`;
}

function formatConfidence(
  confidence: number | null | undefined,
  isFallback: boolean | null | undefined,
): string {
  if (confidence === null || confidence === undefined) return "-";
  const pct = Math.round(confidence * 100);
  return isFallback ? `${pct}% (fallback)` : `${pct}%`;
}

/**
 * Mapper Geminis severity-streng til en CSS-klasse, så cellen kan farves.
 * Klasserne defineres i styles.css under .ai-severity-*.
 */
function aiSeverityClass(severity: string | null | undefined): string {
  if (!severity) return "ai-severity-none";
  return `ai-severity-${severity.toLowerCase()}`;
}
