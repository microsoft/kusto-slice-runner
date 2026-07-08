import { copyFile, mkdir, stat } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";

const scriptDirectory = path.dirname(fileURLToPath(import.meta.url));
const repositoryRoot = path.resolve(scriptDirectory, "..");
const targetDirectory = path.join(
  repositoryRoot,
  "src",
  "KoLite.LocalApp",
  "wwwroot",
  "lib",
  "marked",
);

const assets = [
  {
    source: path.join(repositoryRoot, "node_modules", "marked", "lib", "marked.umd.js"),
    target: path.join(targetDirectory, "marked.umd.js"),
  },
];

async function requireFile(filePath) {
  let fileStats;

  try {
    fileStats = await stat(filePath);
  } catch (error) {
    if (error.code === "ENOENT") {
      throw new Error(`Expected Marked asset was not found: ${filePath}`);
    }

    throw error;
  }

  if (!fileStats.isFile()) {
    throw new Error(`Expected Marked asset path is not a file: ${filePath}`);
  }
}

await mkdir(targetDirectory, { recursive: true });

for (const asset of assets) {
  await requireFile(asset.source);
  await copyFile(asset.source, asset.target);

  console.log(`Copied ${path.relative(repositoryRoot, asset.source)} to ${path.relative(repositoryRoot, asset.target)}`);
}

