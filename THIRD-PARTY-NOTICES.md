# Third-party notices

Kusto Slice Runner uses the following third-party JavaScript packages for dashboard assets.
The root [NOTICE](NOTICE) contains supplied license texts and the restored application
NuGet inventory. This file is a browser-asset summary, not the complete release SBOM.

| Package | Version | License | Source |
| --- | --- | --- | --- |
| Chart.js | 4.5.1 | MIT | `package-lock.json`, `src\KoLite.LocalApp\wwwroot\lib\chartjs\chart.umd.min.js` |
| @kurkle/color | 0.3.4 | MIT | Transitive dependency of Chart.js in `package-lock.json` |
| Cytoscape.js | 3.34.3 | MIT | `package-lock.json`, `src\KoLite.LocalApp\wwwroot\lib\cytoscape\cytoscape.min.js` |
| cytoscape-dagre | 4.0.1 | MIT | `package-lock.json`, `src\KoLite.LocalApp\wwwroot\lib\cytoscape\cytoscape-dagre.js` (bundles dagre/graphlib, MIT) |
| marked | 18.0.12 | MIT | `package-lock.json`, `src\KoLite.LocalApp\wwwroot\lib\marked\marked.umd.js` (renders the Copilot failure analysis) |
| jsdom | 30.0.1 | MIT | Development-only DOM simulation for `npm run test:js` |
| Playwright | 1.60.0 | Apache-2.0 | Development-only documentation screenshot capture; `package-lock.json` |
| playwright-core | 1.60.0 | Apache-2.0 | Development-only Playwright browser driver; `package-lock.json` |

The Chart.js, Cytoscape.js, and marked browser bundles (and the Chart.js source map) are tracked under `src\KoLite.LocalApp\wwwroot\lib` so the .NET app can run without requiring npm during every build. Refresh them with:

```powershell
npm ci
```

If additional third-party runtime assets are added, update this file and keep the dependency lock file in sync.

The screenshot workflow separately downloads Chromium Headless Shell and its support
tools into an ignored developer artifacts directory. These browser executables,
Playwright packages, and the .NET screenshot host are not included in Kusto Slice Runner release
packages. See [the screenshot recipe](docs/screenshots.md).
