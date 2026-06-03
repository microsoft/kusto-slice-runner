# Third-party notices

KO Lite uses the following third-party JavaScript packages for dashboard assets.

| Package | Version | License | Source |
| --- | --- | --- | --- |
| Chart.js | 4.5.1 | MIT | `package-lock.json`, `src\KoLite.LocalApp\wwwroot\lib\chartjs\chart.umd.min.js` |
| @kurkle/color | 0.3.4 | MIT | Transitive dependency of Chart.js in `package-lock.json` |

The Chart.js browser bundle and source map are tracked under `src\KoLite.LocalApp\wwwroot\lib\chartjs` so the .NET app can run without requiring npm during every build. Refresh them with:

```powershell
npm ci
```

If additional third-party runtime assets are added, update this file and keep the dependency lock file in sync.
