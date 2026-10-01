# Third-party notices

Kusto Slice Runner uses the following third-party packages and copied SVG assets.
The root [NOTICE](NOTICE) contains supplied license texts and the restored application
NuGet inventory. This file is a browser-asset summary, not the complete release SBOM.

| Package | Version | License | Source |
| --- | --- | --- | --- |
| Chart.js | 4.5.1 | MIT | `package-lock.json`, `src\Ksr.LocalApp\wwwroot\lib\chartjs\chart.umd.min.js` |
| @kurkle/color | 0.3.4 | MIT | Transitive dependency of Chart.js in `package-lock.json` |
| Cytoscape.js | 3.34.3 | MIT | `package-lock.json`, `src\Ksr.LocalApp\wwwroot\lib\cytoscape\cytoscape.min.js` |
| cytoscape-dagre | 4.0.1 | MIT | `package-lock.json`, `src\Ksr.LocalApp\wwwroot\lib\cytoscape\cytoscape-dagre.js` (bundles dagre/graphlib, MIT) |
| @dagrejs/dagre | 3.0.0 | MIT | Version constant in the cytoscape-dagre bundle; [upstream](https://github.com/dagrejs/dagre/tree/v3.0.0) |
| @dagrejs/graphlib | 4.0.1 | MIT | Version constant in the cytoscape-dagre bundle; [upstream](https://github.com/dagrejs/graphlib/tree/v4.0.1) |
| Bootstrap Icons | Adapted subset; original copy revision unrecorded | MIT | `src\Ksr.LocalApp\Pages\Shared\_IconSprite.cshtml`; [upstream license reference](https://github.com/twbs/icons/blob/v1.13.1/LICENSE) |
| marked | 18.0.13 | MIT | `package-lock.json`, `src\Ksr.LocalApp\wwwroot\lib\marked\marked.umd.js` (renders the Copilot failure analysis) |
| jsdom | 30.1.0 | MIT | Development-only DOM simulation for `npm run test:js` |
| Playwright | 1.63.0 | Apache-2.0 | Development-only documentation screenshot capture; `package-lock.json` |
| playwright-core | 1.63.0 | Apache-2.0 | Development-only Playwright browser driver; `package-lock.json` |

The Chart.js, Cytoscape.js, and marked browser bundles (and the Chart.js source map) are tracked under `src\Ksr.LocalApp\wwwroot\lib` so the .NET app can run without requiring npm during every build. Refresh them with:

```powershell
npm ci
```

The cytoscape-dagre 4.0.1 package's upstream banner says 4.0.0; the distributed
file is preserved unchanged. Dagre/Graphlib are bundled upstream build
dependencies, so the application lock file alone does not enumerate them.
Bootstrap's copied/adapted paths retain their original authors' attribution;
the sprite is not claimed to be an unmodified v1.13.1 distribution.

If additional third-party runtime assets are added, update this file and `NOTICE`,
preserve their original licenses, and keep the dependency lock file in sync.

Kusto SDK packages keep their EULA rather than adopting this project's MIT
license. Source notices do not replace the release owner's redistribution review.

The screenshot workflow separately downloads Chromium Headless Shell and its support
tools into an ignored developer artifacts directory. These browser executables,
Playwright packages, and the .NET screenshot host are not included in Kusto Slice Runner release
packages. See [the screenshot recipe](docs/screenshots.md).
