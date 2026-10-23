# Web UI: Angular build, served by Caddy (the Caddyfile comes from deploy/ as a volume).
FROM node:22-alpine AS build
WORKDIR /src
# npm 11 is required (peer dependency bug of npm 10.9).
RUN npm install -g npm@11
COPY package.json package-lock.json ./
RUN npm ci
COPY . .
RUN npx ng build --configuration production

FROM caddy:2-alpine
COPY --from=build /src/dist/web/browser /srv
