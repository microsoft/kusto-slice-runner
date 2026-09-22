// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

import { copyFile, mkdir, stat } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";

const scriptDirectory = path.dirname(fileURLToPath(import.meta.url));
const repositoryRoot = path.resolve(scriptDirectory, "..");
const targetDirectory = path.join(
  repositoryRoot,
  "src",
  "Ksr.LocalApp",
  "wwwroot",
  "lib",
  "cytoscape",
);

// cytoscape-dagre bundles its own graphlib/dagre, so no separate dagre asset is needed.
const assets = [
  {
    source: path.join(repositoryRoot, "node_modules", "cytoscape", "dist", "cytoscape.min.js"),
    target: path.join(targetDirectory, "cytoscape.min.js"),
  },
  {
    source: path.join(repositoryRoot, "node_modules", "cytoscape-dagre", "dist", "cytoscape-dagre.js"),
    target: path.join(targetDirectory, "cytoscape-dagre.js"),
  },
];

async function requireFile(filePath) {
  let fileStats;

  try {
    fileStats = await stat(filePath);
  } catch (error) {
    if (error.code === "ENOENT") {
      throw new Error(`Expected Cytoscape asset was not found: ${filePath}`);
    }

    throw error;
  }

  if (!fileStats.isFile()) {
    throw new Error(`Expected Cytoscape asset path is not a file: ${filePath}`);
  }
}

await mkdir(targetDirectory, { recursive: true });

for (const asset of assets) {
  await requireFile(asset.source);
  await copyFile(asset.source, asset.target);

  console.log(`Copied ${path.relative(repositoryRoot, asset.source)} to ${path.relative(repositoryRoot, asset.target)}`);
}
