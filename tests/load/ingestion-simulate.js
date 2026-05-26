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

// Tom body — servicen loader automatisk TestData/sample-batch.json
// (kanal 20, korrekt integer-format). Jf. IngestionEndpoints.cs linje ~45.
const baseUrl = __ENV.GATEWAY_BASE_URL || 'http://localhost:5000';

export default function () {
    const correlationId = uuidv4();
    const res = http.post(
        `${baseUrl}/ingestion/simulate`,
        null,
        {
            headers: {
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
