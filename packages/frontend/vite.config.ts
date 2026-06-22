import { defineConfig, loadEnv } from 'vite';
import react from '@vitejs/plugin-react';
import { VitePWA } from 'vite-plugin-pwa';

export default defineConfig(({ mode }) => {
  const env = loadEnv(mode, '.', '');
  const backendUrl = env.VITE_DEV_BACKEND_URL || 'http://127.0.0.1:5080';

  return {
    plugins: [
      react(),
      VitePWA({
        registerType: 'autoUpdate',
        includeAssets: ['pwa.svg'],
        manifest: {
          name: 'MusicDecrypto',
          short_name: 'MusicDecrypto',
          description: 'MusicDecrypto frontend client',
          theme_color: '#0f172a',
          background_color: '#f8fafc',
          display: 'standalone',
          start_url: '/',
          icons: [
            {
              src: '/pwa.svg',
              sizes: 'any',
              type: 'image/svg+xml',
              purpose: 'any maskable'
            }
          ]
        },
        workbox: {
          cleanupOutdatedCaches: true,
          clientsClaim: true,
          skipWaiting: true,
          navigateFallbackDenylist: [
            /^\/api(?:\/|$)/,
            /^\/files(?:\/|$)/,
            /^\/update(?:\/|$)/,
            /^\/healthz$/
          ],
          runtimeCaching: [
            {
              urlPattern: /^\/api(?:\/|$)/,
              handler: 'NetworkOnly',
              options: {
                cacheName: 'api-network-only'
              }
            },
            {
              urlPattern: /^\/files(?:\/|$)/,
              handler: 'NetworkOnly',
              options: {
                cacheName: 'tus-network-only'
              }
            },
            {
              urlPattern: /^\/update(?:\/|$)/,
              handler: 'NetworkOnly',
              options: {
                cacheName: 'update-network-only'
              }
            },
            {
              urlPattern: /^\/healthz$/,
              handler: 'NetworkOnly',
              options: {
                cacheName: 'health-network-only'
              }
            }
          ]
        },
        devOptions: {
          enabled: true,
          suppressWarnings: true
        }
      })
    ],
    server: {
      port: 5173,
      proxy: {
        '/api': backendUrl,
        '/files': backendUrl,
        '/healthz': backendUrl,
        '/update': backendUrl
      }
    }
  };
});
