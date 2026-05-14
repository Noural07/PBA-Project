# syntax=docker/dockerfile:1.7
# ----------------------------------------------------------------------------
# AlertFeed frontend – multi-stage build.
#  1) Node 20 stage transpilerer TypeScript og bundler statiske assets.
#  2) Nginx stage serverer det færdige dist/-output på port 80.
#
# VITE_ALERTING_BASE_URL er en build-time variable og indlejres i bundlen.
# Konsekvensen er, at frontend-imaget er specifikt for ét deployment-target;
# det er en bevidst trade-off frem for at læse URL'en runtime fra en
# /config.json, fordi systemet i Phase 4 udelukkende køres lokalt.
# ----------------------------------------------------------------------------

FROM node:20-alpine AS build
WORKDIR /app

ARG VITE_ALERTING_BASE_URL
ENV VITE_ALERTING_BASE_URL=$VITE_ALERTING_BASE_URL

COPY package.json package-lock.json* ./
RUN npm install --no-audit --no-fund

COPY tsconfig.json vite.config.ts index.html ./
COPY src ./src

RUN npm run build

# ----------------------------------------------------------------------------
FROM nginx:1.27-alpine AS runtime
COPY --from=build /app/dist /usr/share/nginx/html
COPY nginx.conf /etc/nginx/conf.d/default.conf
EXPOSE 80
