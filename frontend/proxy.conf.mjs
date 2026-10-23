// Development proxy: the browser asks everything from the Angular dev server, so the .NET API needs no CORS.
// In production Caddy does the same (deploy/Caddyfile): /api and /health → API.
// Override the target with an environment variable: API_URL=http://localhost:5099 npm start
const api = process.env.API_URL ?? 'http://localhost:5032';

export default {
  '/api': { target: api, secure: false },
  '/health': { target: api, secure: false },
};
