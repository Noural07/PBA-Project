-- ============================================================================
-- Database-per-Service initialisering for PBA-trendlog-platformen.
--
-- Filen udføres automatisk af Postgres' officielle image, da den ligger i
-- /docker-entrypoint-initdb.d/. Den kører KUN ved første initialisering af
-- volumen (postgres-data). Subsekvente container-starter springer initdb
-- over, så scriptet skal være idempotent for at sikre, at en eventuel
-- manuel kørsel ikke kaster fejl.
--
-- Akademisk rationale (jf. Newman, *Building Microservices*, kap. 4 og
-- Richardson, *Microservices Patterns*, pattern 4): hver bounded context
-- ejer sit eget skema, så ændringer i én tjenestes datalag ikke implicit
-- kobler andre tjenester. Vi adskiller derfor AnalyzerService og
-- AlertingService som separate logiske databaser i samme cluster.
-- ============================================================================

SELECT 'CREATE DATABASE pba_analyzer OWNER pba'
WHERE NOT EXISTS (SELECT 1 FROM pg_database WHERE datname = 'pba_analyzer')\gexec

SELECT 'CREATE DATABASE pba_alerting OWNER pba'
WHERE NOT EXISTS (SELECT 1 FROM pg_database WHERE datname = 'pba_alerting')\gexec

GRANT ALL PRIVILEGES ON DATABASE pba_analyzer TO pba;
GRANT ALL PRIVILEGES ON DATABASE pba_alerting TO pba;
