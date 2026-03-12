# Virtual Lab UI

Angular standalone frontend scaffold for the virtual lab.

## Commands

- `npm install`
- `npm start`
- `npm run build`
- `npm run build:production`
- `npm run build:azure`
- `npm test -- --watch=false --browsers=ChromeHeadless`
- `npm run lint`

The dev server proxies API, FCC, callback, and SignalR traffic to `http://localhost:5099`.

## Runtime configuration

The deployed app reads `public/assets/config/runtime-config.json` on startup.

- Keep `apiBaseUrl` empty for local development behind the Angular proxy.
- Set `apiBaseUrl` to the Azure Web App origin for Azure Static Web Apps deployment.
- Leave `signalRHubUrl` empty unless SignalR needs a different origin or path than the API.

See [`../../docs/azure-deployment.md`](/mnt/c/Users/a0812/fccmiddleware/VirtualLab/docs/azure-deployment.md) for the Azure deployment contract and required environment settings.
