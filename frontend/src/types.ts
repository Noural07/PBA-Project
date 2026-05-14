/**
 * Strukturen af et konsolideret alarm-event som leveret af AlertingService'
 * SSE-stream. Felterne matcher kontrakten i AlertingService.Domain.ConsolidatedAlert.
 *
 * Bemærk at AI-felterne er valgfrie: en kritisk alarm uden tilhørende
 * AI-klassifikation udsendes også, og AI-felterne udfyldes når
 * StopReasonClassified-eventet ankommer for samme korrelations-ID.
 *
 * Fase C-udvidelser: aiSubcategory (dansk underkategori), aiSeverity
 * (Low/Medium/High/Critical), aiRecommendedAction (kort dansk anbefaling)
 * og aiLatencyMs (Gemini-kaldets varighed i millisekunder).
 *
 * Fase 4-CQRS-udvidelse: aiOriginalReason markerer hvilket stop-events
 * fri-tekst der har leveret de aktuelt-viste AI-felter. Anvendes både til
 * intern match-logik på server-siden og som drill-down-nøgle, så frontend
 * kan vise "viste AI-felter hører til reason X".
 */
export interface ConsolidatedAlert {
  alertId: string;
  correlationId: string;
  timestamp: string;
  channelId: number;
  severity: string;
  rule: string;
  description: string;
  totalDowntimeMinutes: number;
  topReason?: string | null;
  orderId?: string | null;
  aiCategory?: string | null;
  aiSubcategory?: string | null;
  aiStandardizedReason?: string | null;
  aiSeverity?: string | null;
  aiRecommendedAction?: string | null;
  aiConfidence?: number | null;
  aiLatencyMs?: number | null;
  aiIsFallback?: boolean | null;
  aiOriginalReason?: string | null;
}

/**
 * Én række i audit-loggen `classified_stop_reasons`, leveret af
 * `GET /alerts/{correlationId}/classifications`. Repræsenterer ét specifikt
 * stop-events AI-klassifikation, uafhængigt af om det stop-event vandt
 * visningen i AlertFeed eller blot blev persisteret som audit-spor.
 */
export interface AlertClassification {
  eventId: string;
  stopEventId: string;
  occurredAt: string;
  channelId: number;
  originalReason: string;
  category: string;
  subcategory: string;
  standardizedReason: string;
  severity: string;
  recommendedAction: string;
  confidence: number;
  latencyMs: number;
  isFallback: boolean;
}

/**
 * Indpakning af klassifikations-listen for én korrelations-ID.
 */
export interface AlertClassificationsResponse {
  correlationId: string;
  count: number;
  classifications: AlertClassification[];
}
