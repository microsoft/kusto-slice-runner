// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

import { copyFile, mkdir, stat } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";

const scriptDirectory = path.dirname(fileURLToPath(import.meta.url));
const repositoryRoot = path.resolve(scriptDirectory, "..");
const sourceDirectory = path.join(repositoryRoot, "node_modules", "chart.js", "dist");
const targetDirectory = path.join(
  repositoryRoot,
  "src",
  "Ksr.LocalApp",
  "wwwroot",
  "lib",
  "chartjs",
);
const files = ["chart.umd.min.js", "chart.umd.min.js.map"];

async function requireFile(filePath) {
  let fileStats;

  try {
    fileStats = await stat(filePath);
  } catch (error) {
    if (error.code === "ENOENT") {
      throw new Error(`Expected Chart.js asset was not found: ${filePath}`);
    }

    throw error;
  }

  if (!fileStats.isFile()) {
    throw new Error(`Expected Chart.js asset path is not a file: ${filePath}`);
  }
}

await mkdir(targetDirectory, { recursive: true });

for (const file of files) {
  const source = path.join(sourceDirectory, file);
  const target = path.join(targetDirectory, file);

  await requireFile(source);
  await copyFile(source, target);

  console.log(`Copied ${path.relative(repositoryRoot, source)} to ${path.relative(repositoryRoot, target)}`);
}
