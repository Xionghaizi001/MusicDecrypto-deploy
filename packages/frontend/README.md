# MusicDecrypto Frontend

React + TypeScript + Vite frontend shell for the MusicDecrypto backend.

## Scripts

```bash
pnpm install
pnpm dev
pnpm build
pnpm preview
```

The dev server proxies `/api`, `/files`, `/healthz`, and `/update` to the backend.
Override the backend URL with:

```bash
VITE_DEV_BACKEND_URL=http://127.0.0.1:5080 pnpm dev
```

For production builds, set `VITE_API_BASE_URL` when the frontend and backend do
not share the same origin.

## Structure

- `src/main.tsx`: React mount and PWA registration only.
- `src/App.tsx`: temporary app shell for upload, jobs, and download wiring.
- `src/lib/api.ts`: backend API client.
- `src/lib/uploader.ts`: Uppy + tus upload integration.
- `src/lib/file-system.ts`: browser file picker/download helpers.
- `src/types/backend.ts`: shared backend response types.
