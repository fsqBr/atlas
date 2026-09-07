// Picks a folder with the browser's native dialog, zips it client-side (skipping build output,
// packages and VCS internals) and uploads it. The server never touches the user's disk.
import JSZip from "jszip";

const SKIP_DIRS = new Set([".git", ".vs", ".idea", "bin", "obj", "node_modules", "packages", "TestResults", "dist", ".nuget", "artifacts"]);
const MAX_FILE_BYTES = 25 * 1024 * 1024;

export interface UploadProgress {
  phase: "reading" | "zipping" | "uploading" | "done";
  files: number;
  bytes: number;
  percent?: number;
}

export interface PickedFolder {
  name: string;
  files: { path: string; file: File }[];
  skipped: number;
}

export function supportsDirectoryPicker(): boolean {
  return typeof window !== "undefined" && "showDirectoryPicker" in window;
}

/** Native directory dialog (Chrome/Edge). Throws AbortError when the user cancels. */
export async function pickFolder(onProgress?: (p: UploadProgress) => void): Promise<PickedFolder> {
  const picker = (window as unknown as { showDirectoryPicker: (o?: { mode?: string }) => Promise<FileSystemDirectoryHandle> }).showDirectoryPicker;
  const root = await picker({ mode: "read" });
  const files: { path: string; file: File }[] = [];
  let skipped = 0;
  let bytes = 0;

  async function walk(dir: FileSystemDirectoryHandle, prefix: string) {
    const entries = (dir as unknown as { values: () => AsyncIterable<FileSystemHandle> }).values();
    for await (const handle of entries) {
      if (handle.kind === "directory") {
        if (SKIP_DIRS.has(handle.name) || handle.name.startsWith(".")) {
          skipped++;
          continue;
        }
        await walk(handle as FileSystemDirectoryHandle, `${prefix}${handle.name}/`);
      } else {
        const file = await (handle as FileSystemFileHandle).getFile();
        if (file.size > MAX_FILE_BYTES) {
          skipped++;
          continue;
        }
        files.push({ path: `${prefix}${handle.name}`, file });
        bytes += file.size;
        if (files.length % 200 === 0) onProgress?.({ phase: "reading", files: files.length, bytes });
      }
    }
  }

  await walk(root, "");
  onProgress?.({ phase: "reading", files: files.length, bytes });
  return { name: root.name, files, skipped };
}

/** Fallback for browsers without showDirectoryPicker: <input type="file" webkitdirectory>. */
export function filesFromInput(list: FileList): PickedFolder {
  const files: { path: string; file: File }[] = [];
  let skipped = 0;
  let name = "";
  for (const file of Array.from(list)) {
    const rel = (file as File & { webkitRelativePath?: string }).webkitRelativePath || file.name;
    const parts = rel.split("/");
    if (!name && parts.length > 1) name = parts[0];
    const inner = parts.length > 1 ? parts.slice(1) : parts;
    if (inner.slice(0, -1).some((d) => SKIP_DIRS.has(d) || d.startsWith(".")) || file.size > MAX_FILE_BYTES) {
      skipped++;
      continue;
    }
    files.push({ path: inner.join("/"), file });
  }
  return { name: name || "upload", files, skipped };
}

export async function zipAndUpload(folder: PickedFolder, onProgress?: (p: UploadProgress) => void): Promise<{ uploadId: string; name: string; bytes: number; files: number }> {
  const zip = new JSZip();
  for (const { path, file } of folder.files) {
    zip.file(path, file, { date: new Date(file.lastModified) });
  }

  const blob = await zip.generateAsync({ type: "blob", compression: "DEFLATE", compressionOptions: { level: 6 } }, (meta) =>
    onProgress?.({ phase: "zipping", files: folder.files.length, bytes: 0, percent: Math.round(meta.percent) }),
  );

  onProgress?.({ phase: "uploading", files: folder.files.length, bytes: blob.size, percent: 0 });
  const form = new FormData();
  form.append("name", folder.name);
  form.append("files", String(folder.files.length));
  form.append("archive", blob, `${folder.name}.zip`);

  const response = await fetch("/api/uploads", { method: "POST", body: form, headers: authHeader() });
  if (!response.ok) {
    let detail = response.statusText;
    try {
      const body = (await response.json()) as { error?: string };
      if (body.error) detail = body.error;
    } catch {
      /* no body */
    }
    throw new Error(detail);
  }
  onProgress?.({ phase: "done", files: folder.files.length, bytes: blob.size, percent: 100 });
  return (await response.json()) as { uploadId: string; name: string; bytes: number; files: number };
}

function authHeader(): Record<string, string> {
  try {
    // Lazy import to avoid a circular dependency with api.ts.
    const token = (window as unknown as { __atlasToken?: () => string | null }).__atlasToken?.();
    return token ? { Authorization: `Bearer ${token}` } : {};
  } catch {
    return {};
  }
}
