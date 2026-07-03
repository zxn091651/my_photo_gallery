import { createServer } from 'node:http';
import { stat, readdir, realpath, access } from 'node:fs/promises';
import { constants, createReadStream } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawn } from 'node:child_process';

const __dirname = path.dirname(fileURLToPath(import.meta.url));

const PORT = Number(process.env.PORT || 8787);
const HOST = process.env.HOST || '0.0.0.0';
const MEDIA_ROOT = path.resolve(process.env.GALLERY_ROOT || 'F:\\影像备份');
const DRIVE_LETTER = process.env.GALLERY_DRIVE || path.parse(MEDIA_ROOT).root.replace(/[:\\\/]/g, '') || 'F';
const EXPECTED_VOLUME = process.env.GALLERY_VOLUME || 'WD_BLACK';
const WEB_ROOT = path.join(__dirname, 'web');

const IMAGE_EXTENSIONS = new Set([
  '.jpg',
  '.jpeg',
  '.png',
  '.gif',
  '.webp',
  '.bmp',
  '.avif',
  '.tif',
  '.tiff',
  '.heic',
  '.heif'
]);

const VIDEO_EXTENSIONS = new Set([
  '.mp4',
  '.webm',
  '.mov',
  '.m4v',
  '.avi',
  '.mkv',
  '.mts',
  '.m2ts',
  '.3gp'
]);

const MIME_TYPES = new Map([
  ['.html', 'text/html; charset=utf-8'],
  ['.css', 'text/css; charset=utf-8'],
  ['.js', 'text/javascript; charset=utf-8'],
  ['.json', 'application/json; charset=utf-8'],
  ['.svg', 'image/svg+xml'],
  ['.png', 'image/png'],
  ['.jpg', 'image/jpeg'],
  ['.jpeg', 'image/jpeg'],
  ['.gif', 'image/gif'],
  ['.webp', 'image/webp'],
  ['.bmp', 'image/bmp'],
  ['.avif', 'image/avif'],
  ['.tif', 'image/tiff'],
  ['.tiff', 'image/tiff'],
  ['.heic', 'image/heic'],
  ['.heif', 'image/heif'],
  ['.mp4', 'video/mp4'],
  ['.webm', 'video/webm'],
  ['.mov', 'video/quicktime'],
  ['.m4v', 'video/mp4'],
  ['.avi', 'video/x-msvideo'],
  ['.mkv', 'video/x-matroska'],
  ['.mts', 'video/mp2t'],
  ['.m2ts', 'video/mp2t'],
  ['.3gp', 'video/3gpp']
]);

let rootRealPathCache = null;

function sendJson(response, statusCode, payload) {
  const body = JSON.stringify(payload);
  response.writeHead(statusCode, {
    'Content-Type': 'application/json; charset=utf-8',
    'Content-Length': Buffer.byteLength(body),
    'Access-Control-Allow-Origin': '*',
    'Access-Control-Allow-Methods': 'GET, HEAD, OPTIONS',
    'Access-Control-Allow-Headers': 'Content-Type, Range',
    'Access-Control-Expose-Headers': 'Accept-Ranges, Content-Length, Content-Range'
  });
  response.end(body);
}

function sendError(response, statusCode, message, details = undefined) {
  sendJson(response, statusCode, { ok: false, error: message, details });
}

function writeCors(response) {
  response.setHeader('Access-Control-Allow-Origin', '*');
  response.setHeader('Access-Control-Allow-Methods', 'GET, HEAD, OPTIONS');
  response.setHeader('Access-Control-Allow-Headers', 'Content-Type, Range');
  response.setHeader('Access-Control-Expose-Headers', 'Accept-Ranges, Content-Length, Content-Range');
}

function normalizeRelativePath(value = '') {
  const decoded = String(value).replaceAll('\\', '/').replace(/^\/+/, '');
  return decoded === '.' ? '' : decoded;
}

async function getRootRealPath() {
  if (rootRealPathCache) return rootRealPathCache;
  rootRealPathCache = await realpath(MEDIA_ROOT);
  return rootRealPathCache;
}

async function resolveInsideMediaRoot(relativePath = '') {
  const rootReal = await getRootRealPath();
  const target = path.resolve(rootReal, normalizeRelativePath(relativePath));
  const targetReal = await realpath(target);
  const rootWithSeparator = rootReal.endsWith(path.sep) ? rootReal : `${rootReal}${path.sep}`;
  const lowerTarget = targetReal.toLowerCase();
  const lowerRoot = rootReal.toLowerCase();
  const lowerRootWithSeparator = rootWithSeparator.toLowerCase();

  if (lowerTarget !== lowerRoot && !lowerTarget.startsWith(lowerRootWithSeparator)) {
    throw new Error('Path escapes media root.');
  }

  return targetReal;
}

function toRelativeUrlPath(rootPath, absolutePath) {
  return path.relative(rootPath, absolutePath).split(path.sep).join('/');
}

function isMediaFile(fileName) {
  const extension = path.extname(fileName).toLowerCase();
  if (IMAGE_EXTENSIONS.has(extension)) return 'image';
  if (VIDEO_EXTENSIONS.has(extension)) return 'video';
  return null;
}

async function pathExists(filePath) {
  try {
    await access(filePath, constants.F_OK);
    return true;
  } catch {
    return false;
  }
}

function getVolumeName() {
  return new Promise((resolve) => {
    const command = `try { (Get-Volume -DriveLetter '${DRIVE_LETTER}' -ErrorAction Stop).FileSystemLabel } catch { '' }`;
    const child = spawn('powershell.exe', ['-NoProfile', '-Command', command], { windowsHide: true });
    let output = '';

    child.stdout.on('data', (data) => {
      output += data.toString('utf8');
    });

    child.on('error', () => resolve(''));
    child.on('close', () => resolve(output.trim()));
  });
}

async function getStatus() {
  const driveRoot = `${DRIVE_LETTER}:\\`;
  const [drivePresent, rootPresent, volumeName] = await Promise.all([
    pathExists(driveRoot),
    pathExists(MEDIA_ROOT),
    getVolumeName()
  ]);

  if (!rootPresent) {
    rootRealPathCache = null;
  }

  return {
    ok: true,
    computerOnline: true,
    driveLetter: DRIVE_LETTER,
    drivePresent,
    expectedVolume: EXPECTED_VOLUME,
    volumeName,
    volumeMatches: !volumeName || volumeName === EXPECTED_VOLUME,
    mediaRoot: MEDIA_ROOT,
    mediaRootPresent: rootPresent
  };
}

async function listFolders() {
  const root = await resolveInsideMediaRoot('');
  const folders = [];
  const stack = [{ absolutePath: root, relativePath: '' }];

  while (stack.length) {
    const current = stack.pop();
    const entries = await readdir(current.absolutePath, { withFileTypes: true });
    const childDirectories = entries
      .filter((entry) => entry.isDirectory())
      .sort((a, b) => a.name.localeCompare(b.name, 'zh-Hans-CN'));

    for (const entry of childDirectories) {
      const absolutePath = path.join(current.absolutePath, entry.name);
      const relativePath = current.relativePath ? `${current.relativePath}/${entry.name}` : entry.name;
      folders.push({
        name: entry.name,
        path: relativePath,
        depth: relativePath.split('/').length - 1
      });
    }

    for (const entry of childDirectories.toReversed()) {
      const absolutePath = path.join(current.absolutePath, entry.name);
      const relativePath = current.relativePath ? `${current.relativePath}/${entry.name}` : entry.name;
      stack.push({ absolutePath, relativePath });
    }
  }

  return folders;
}

async function listMedia(folderPath) {
  const root = await getRootRealPath();
  const folder = await resolveInsideMediaRoot(folderPath);
  const folderStat = await stat(folder);
  if (!folderStat.isDirectory()) {
    throw new Error('The requested path is not a folder.');
  }

  const entries = await readdir(folder, { withFileTypes: true });
  const folders = [];
  const files = [];

  for (const entry of entries) {
    const absolutePath = path.join(folder, entry.name);
    const relativePath = toRelativeUrlPath(root, absolutePath);

    if (entry.isDirectory()) {
      folders.push({
        name: entry.name,
        path: relativePath
      });
      continue;
    }

    if (!entry.isFile()) continue;

    const type = isMediaFile(entry.name);
    if (!type) continue;

    const fileStat = await stat(absolutePath);
    files.push({
      name: entry.name,
      path: relativePath,
      type,
      size: fileStat.size,
      modifiedAt: fileStat.mtime.toISOString(),
      viewUrl: `/api/file?path=${encodeURIComponent(relativePath)}`,
      downloadUrl: `/api/download?path=${encodeURIComponent(relativePath)}`
    });
  }

  folders.sort((a, b) => a.name.localeCompare(b.name, 'zh-Hans-CN'));
  files.sort((a, b) => a.name.localeCompare(b.name, 'zh-Hans-CN'));

  return { folders, files };
}

async function streamFile(request, response, relativePath, asDownload) {
  const filePath = await resolveInsideMediaRoot(relativePath);
  const fileStat = await stat(filePath);

  if (!fileStat.isFile()) {
    sendError(response, 404, 'File not found.');
    return;
  }

  const extension = path.extname(filePath).toLowerCase();
  const contentType = MIME_TYPES.get(extension) || 'application/octet-stream';
  const fileName = path.basename(filePath);
  const range = request.headers.range;

  writeCors(response);
  response.setHeader('Accept-Ranges', 'bytes');
  response.setHeader('Content-Type', contentType);
  response.setHeader('Cache-Control', 'private, max-age=3600');

  if (asDownload) {
    response.setHeader('Content-Disposition', `attachment; filename*=UTF-8''${encodeURIComponent(fileName)}`);
  }

  if (!range) {
    response.writeHead(200, { 'Content-Length': fileStat.size });
    if (request.method === 'HEAD') {
      response.end();
      return;
    }
    createReadStream(filePath).pipe(response);
    return;
  }

  const match = range.match(/^bytes=(\d*)-(\d*)$/);
  if (!match) {
    response.writeHead(416, { 'Content-Range': `bytes */${fileStat.size}` });
    response.end();
    return;
  }

  const start = match[1] ? Number(match[1]) : 0;
  const end = match[2] ? Number(match[2]) : fileStat.size - 1;

  if (Number.isNaN(start) || Number.isNaN(end) || start > end || start >= fileStat.size) {
    response.writeHead(416, { 'Content-Range': `bytes */${fileStat.size}` });
    response.end();
    return;
  }

  const safeEnd = Math.min(end, fileStat.size - 1);
  response.writeHead(206, {
    'Content-Length': safeEnd - start + 1,
    'Content-Range': `bytes ${start}-${safeEnd}/${fileStat.size}`
  });

  if (request.method === 'HEAD') {
    response.end();
    return;
  }

  createReadStream(filePath, { start, end: safeEnd }).pipe(response);
}

async function serveStatic(request, response, requestUrl) {
  let pathname = decodeURIComponent(requestUrl.pathname);
  if (pathname === '/') pathname = '/index.html';

  const target = path.resolve(WEB_ROOT, `.${pathname}`);
  const root = path.resolve(WEB_ROOT);
  const rootWithSeparator = root.endsWith(path.sep) ? root : `${root}${path.sep}`;

  if (target !== root && !target.startsWith(rootWithSeparator)) {
    sendError(response, 403, 'Forbidden.');
    return;
  }

  try {
    let targetStat = await stat(target);
    let finalTarget = target;

    if (targetStat.isDirectory()) {
      finalTarget = path.join(target, 'index.html');
      targetStat = await stat(finalTarget);
    }

    const extension = path.extname(finalTarget).toLowerCase();
    response.writeHead(200, {
      'Content-Type': MIME_TYPES.get(extension) || 'application/octet-stream',
      'Content-Length': targetStat.size
    });

    if (request.method === 'HEAD') {
      response.end();
      return;
    }

    createReadStream(finalTarget).pipe(response);
  } catch {
    const fallback = path.join(WEB_ROOT, 'index.html');
    const fallbackStat = await stat(fallback);
    response.writeHead(200, {
      'Content-Type': 'text/html; charset=utf-8',
      'Content-Length': fallbackStat.size
    });
    createReadStream(fallback).pipe(response);
  }
}

async function handleApi(request, response, requestUrl) {
  if (request.method === 'OPTIONS') {
    writeCors(response);
    response.writeHead(204);
    response.end();
    return;
  }

  if (request.method !== 'GET' && request.method !== 'HEAD') {
    sendError(response, 405, 'Method not allowed.');
    return;
  }

  try {
    if (requestUrl.pathname === '/api/status') {
      sendJson(response, 200, await getStatus());
      return;
    }

    if (requestUrl.pathname === '/api/folders') {
      const status = await getStatus();
      if (!status.mediaRootPresent) {
        sendJson(response, 200, { ok: true, folders: [], status });
        return;
      }
      sendJson(response, 200, { ok: true, folders: await listFolders(), status });
      return;
    }

    if (requestUrl.pathname === '/api/media') {
      const folder = requestUrl.searchParams.get('folder') || '';
      sendJson(response, 200, { ok: true, folder, ...(await listMedia(folder)) });
      return;
    }

    if (requestUrl.pathname === '/api/file' || requestUrl.pathname === '/api/download') {
      const filePath = requestUrl.searchParams.get('path');
      if (!filePath) {
        sendError(response, 400, 'Missing file path.');
        return;
      }
      await streamFile(request, response, filePath, requestUrl.pathname === '/api/download');
      return;
    }

    sendError(response, 404, 'API route not found.');
  } catch (error) {
    const message = error instanceof Error ? error.message : 'Unknown error.';
    sendError(response, 500, 'Request failed.', message);
  }
}

const server = createServer(async (request, response) => {
  const requestUrl = new URL(request.url || '/', `http://${request.headers.host || 'localhost'}`);

  if (requestUrl.pathname.startsWith('/api/')) {
    await handleApi(request, response, requestUrl);
    return;
  }

  await serveStatic(request, response, requestUrl);
});

server.listen(PORT, HOST, () => {
  console.log(`Photo gallery backend: http://127.0.0.1:${PORT}`);
  console.log(`Media root: ${MEDIA_ROOT}`);
});

process.on('SIGINT', () => {
  server.close(() => process.exit(0));
});
