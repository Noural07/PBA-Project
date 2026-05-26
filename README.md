# PBA Trendlog

Vores bachelorprojekt: et lille event-drevet system i .NET 9, der henter data fra Trendlog, analyserer det og sender alarmer videre. Bygget med Docker, RabbitMQ, PostgreSQL, YARP og Gemini.

Hele stacken kører i Docker, så du behøver ikke installere .NET lokalt for at prøve det.

## Hvad du skal bruge

Docker Desktop med Compose v2, og Git. Det er det.

(Hvis du vil køre en enkelt service uden for Docker, skal du også bruge .NET 9 SDK.)

## Sådan kommer du i gang

Klon repoet og hop ind i mappen:

```bash
git clone <repo-url>
cd PBA_Projekt
```

Lav din egen `.env` ud fra skabelonen:

```bash
cp .env.example .env
```

Åbn `.env` og udfyld de tre vigtige nøgler:

- `TRENDLOG_API_KEY` — bearer-token til Trendlog
- `GEMINI_API_KEY` — nøgle til Google Gemini
- Adgangskoderne til Postgres, RabbitMQ og Grafana (find på nogle nye)

Byg og start det hele:

```bash
docker compose up -d --build
```

Første gang tager det et par minutter. Bagefter kan du tjekke at containerne er oppe med `docker compose ps`.

## Hvor ting kører

| Hvad                | Adresse                |
|---------------------|------------------------|
| API Gateway         | http://localhost:5000  |
| IngestionService    | http://localhost:5101  |
| AnalyzerService     | http://localhost:5102  |
| AiService           | http://localhost:5103  |
| AlertingService     | http://localhost:5104  |
| Frontend (AlertFeed)| http://localhost:5173  |
| RabbitMQ UI         | http://localhost:15672 |
| Grafana             | http://localhost:3000  |
| Gatus               | http://localhost:8081  |

Alle services har et `/health`-endpoint, hvis du vil tjekke om de svarer.

## Tests

```bash
dotnet test
```

## Stop det igen

```bash
docker compose down
```

Vil du også slette databaser og data, så tilføj `-v`:

```bash
docker compose down -v
```
