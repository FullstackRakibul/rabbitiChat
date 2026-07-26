// Central frontend configuration. Values come from Vite env files
// (.env, .env.local, .env.production, …) and must be prefixed with VITE_.
export const environment = {
  production: import.meta.env.PROD,
  apiBaseUrl: import.meta.env.VITE_API_BASE_URL || '/api',
}
