# Third-party notices

KO Lite uses the following third-party JavaScript packages for dashboard assets.

| Package | Version | License | Source |
| --- | --- | --- | --- |
| Chart.js | 4.5.1 | MIT | `package-lock.json`, `src\KoLite.LocalApp\wwwroot\lib\chartjs\chart.umd.min.js` |
| @kurkle/color | 0.3.4 | MIT | Transitive dependency of Chart.js in `package-lock.json` |
| Cytoscape.js | 3.34.1 | MIT | `package-lock.json`, `src\KoLite.LocalApp\wwwroot\lib\cytoscape\cytoscape.min.js` |
| cytoscape-dagre | 4.0.0 | MIT | `package-lock.json`, `src\KoLite.LocalApp\wwwroot\lib\cytoscape\cytoscape-dagre.js` (bundles dagre/graphlib, MIT) |
| marked | 18.0.9 | MIT | `package-lock.json`, `src\KoLite.LocalApp\wwwroot\lib\marked\marked.umd.js` (renders the Copilot failure analysis) |
| jsdom | 26.1.0 | MIT | Development-only DOM simulation for `npm run test:js` |

The Chart.js, Cytoscape.js, and marked browser bundles (and the Chart.js source map) are tracked under `src\KoLite.LocalApp\wwwroot\lib` so the .NET app can run without requiring npm during every build. Refresh them with:

```powershell
npm ci
```

If additional third-party runtime assets are added, update this file and keep the dependency lock file in sync.
