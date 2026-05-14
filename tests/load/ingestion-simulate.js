// k6 load-test for /ingestion/simulate.
// Dokumenteret i Bilag F. Køres med:
//   k6 run --vus 10 --duration 60s tests/load/ingestion-simulate.js
// Forudsætter docker-compose-stakken kørende lokalt.

import http from 'k6/http';
import { check, sleep } from 'k6';
import { uuidv4 } from 'https://jslib.k6.io/k6-utils/1.4.0/index.js';

export const options = {
    vus: 10,
    duration: '60s',
    thresholds: {
        // Acceptkriterier afledt af de design-mæssige forventninger til
        // lokal docker-compose-drift. Disse matcher Tabel 1 i kapitel 6.5.1.
        'http_req_duration': ['p(95)<200', 'p(99)<500'],
        'http_req_failed': ['rate<0.01']
    }
};

// Eksempel-payload der følger Trendlog-formatet for kanal XYZ01.
// Aggregering: 'none' for runtime, 'diff' for stoptime — jf. afsnit 5.3.1.
const samplePayload = {
    channelId: 'XYZ01',
    measurements: [
        {
            feed: 'XYZ01_runtime',
            aggregation: 'none',
            value: 1240,
            timestamp: new Date().toISOString()
        },
        {
            feed: 'XYZ01_stoptime',
            aggregation: 'diff',
            value: 35,
            reason: 'Banestyringsfejl',
            timestamp: new Date().toISOString()
        },
        {
            feed: 'XYZ01_cnt',
            aggregation: 'diff',
            value: 482,
            timestamp: new Date().toISOString()
        },
        {
            feed: 'XYZ01_porder',
            aggregation: 'none',
            value: 'PO-10234',
            target: 1000,
            timestamp: new Date().toISOString()
        }
    ]
};

const sampleBatch = JSON.stringify(samplePayload);
const baseUrl = __ENV.GATEWAY_BASE_URL || 'http://localhost:8080';

export default function () {
    const correlationId = uuidv4();
    const res = http.post(
        `${baseUrl}/ingestion/simulate`,
        sampleBatch,
        {
            headers: {
                'Content-Type': 'application/json',
                'X-Correlation-Id': correlationId
            },
            tags: { endpoint: 'ingestion-simulate' }
        }
    );

    check(res, {
        'status is 202 or 200': (r) => r.status === 202 || r.status === 200,
        'response within 500ms': (r) => r.timings.duration < 500
    });

    sleep(0.1);
}
