// Sekundært script: måler end-to-end-latens fra ingestion til SSE-frame.
// Køres parallelt med k6-load-testen i ingestion-simulate.js.
//
// Brug:  node tests/load/sse-latency.js
//
// Forudsætter docker-compose-stakken og at AlertingService eksponerer
// /alerts/stream via gatewayen på http://localhost:8080.
//
// Scriptet logger hver indkommende `event: alert`-frame med tidspunktet
// for ankomst og correlationId; sammenholdt med k6-loggen kan
// end-to-end-latensen p95/p99 efterfølgende beregnes manuelt.

import EventSource from 'eventsource';

const baseUrl = process.env.GATEWAY_BASE_URL || 'http://localhost:8080';
const streamUrl = `${baseUrl}/alerts/stream`;
const startedAt = Date.now();

const samples = [];

const es = new EventSource(streamUrl);

es.addEventListener('alert', (e) => {
    const now = Date.now();
    try {
        const payload = JSON.parse(e.data);
        samples.push({
            arrivedAtMs: now,
            correlationId: payload.correlationId,
            severity: payload.severity,
            rule: payload.rule,
            aiCategory: payload.aiCategory ?? null,
            aiIsFallback: payload.aiIsFallback ?? null
        });
        process.stdout.write(`alert correlationId=${payload.correlationId} arrivedAtMs=${now}\n`);
    } catch (err) {
        process.stderr.write(`parse error: ${err.message}\n`);
    }
});

es.addEventListener('open', () => {
    process.stderr.write(`SSE connected to ${streamUrl} at ${startedAt}\n`);
});

es.addEventListener('error', (err) => {
    process.stderr.write(`SSE error: ${JSON.stringify(err)}\n`);
});

// Skriv et resumé når scriptet termineres med Ctrl+C.
process.on('SIGINT', () => {
    process.stderr.write(`\nReceived SIGINT — total alerts received: ${samples.length}\n`);
    process.exit(0);
});
