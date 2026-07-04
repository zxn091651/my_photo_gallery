import { createServer } from 'node:http';
import { stat, readdir, realpath, access, mkdir, rename } from 'node:fs/promises';
import { constants, createReadStream, createWriteStream } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawn } from 'node:child_process';
import { createHash, timingSafeEqual } from 'node:crypto';
import sharp from 'sharp';
import Busboy from 'busboy';

const __dirname = path.dirname(fileURLToPath(import.meta.url));

const PORT = Number(process.env.PORT || 8787);
const HOST = process.env.HOST || '0.0.0.0';
const CONFIGURED_MEDIA_ROOT = process.env.GALLERY_ROOT || '';
const MEDIA_FOLDER_NAME = process.env.GALLERY_MEDIA_FOLDER || (CONFIGURED_MEDIA_ROOT ? path.basename(CONFIGURED_MEDIA_ROOT) : '影像备份');
const EXPECTED_DISK_SERIAL = normalizeDiskSerial(process.env.GALLERY_DISK_SERIAL || '');
const UPLOAD_PASSWORD = process.env.GALLERY_UPLOAD_PASSWORD || '';
const WEB_ROOT = path.join(__dirname, 'web');
const THUMB_CACHE_ROOT = path.join(__dirname, '.cache', 'thumbs');
const MAX_UPLOAD_BYTES = Number(process.env.GALLERY_MAX_UPLOAD_BYTES || 20 * 1024 * 1024 * 1024);

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

const HIDDEN_MEDIA_EXTENSIONS = new Set([
  '.nef'
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
let rootRealPathCacheSource = '';
let diskSerialStatusCache = null;
let diskSerialStatusCacheAt = 0;
const DISK_STATUS_CACHE_MS = 3000;

function normalizeDiskSerial(value = '') {
  return String(value).replace(/\s+/g, '').toUpperCase();
}

function sendJson(response, statusCode, payload) {
  const body = JSON.stringify(payload);
  response.writeHead(statusCode, {
    'Content-Type': 'application/json; charset=utf-8',
    'Content-Length': Buffer.byteLength(body),
    'Access-Control-Allow-Origin': '*',
    'Access-Control-Allow-Methods': 'GET, HEAD, POST, OPTIONS',
    'Access-Control-Allow-Headers': 'Content-Type, Range, X-Upload-Password',
    'Access-Control-Expose-Headers': 'Accept-Ranges, Content-Length, Content-Range'
  });
  response.end(body);
}

function sendError(response, statusCode, message, details = undefined) {
  sendJson(response, statusCode, { ok: false, error: message, details });
}

function writeCors(response) {
  response.setHeader('Access-Control-Allow-Origin', '*');
  response.setHeader('Access-Control-Allow-Methods', 'GET, HEAD, POST, OPTIONS');
  response.setHeader('Access-Control-Allow-Headers', 'Content-Type, Range, X-Upload-Password');
  response.setHeader('Access-Control-Expose-Headers', 'Accept-Ranges, Content-Length, Content-Range');
}

function uploadPasswordMatches(value = '') {
  if (!UPLOAD_PASSWORD) return false;
  const expected = Buffer.from(UPLOAD_PASSWORD);
  const actual = Buffer.from(String(value));
  return expected.length === actual.length && timingSafeEqual(expected, actual);
}

function normalizeRelativePath(value = '') {
  const decoded = String(value).replaceAll('\\', '/').replace(/^\/+/, '');
  return decoded === '.' ? '' : decoded;
}

function sanitizeFolderName(value = '') {
  const name = String(value).trim();
  if (!name || name === '.' || name === '..') return '';
  if (/[<>:"/\\|?*\x00-\x1f]/.test(name)) return '';
  return name.slice(0, 120);
}

function sanitizeFileName(value = '') {
  const parsed = path.parse(String(value).replaceAll('\\', '/'));
  const baseName = parsed.name
    .replace(/[<>:"/\\|?*\x00-\x1f]/g, '_')
    .replace(/[. ]+$/g, '')
    .trim()
    .slice(0, 160);
  const extension = parsed.ext.toLowerCase();
  const safeBaseName = baseName || 'upload';
  return `${safeBaseName}${extension}`;
}

function mediaRootFromDriveLetter(driveLetter) {
  return path.resolve(`${driveLetter}:\\`, MEDIA_FOLDER_NAME);
}

async function getCurrentMediaRoot() {
  const diskSerialStatus = await getDiskSerialStatus();
  if (!diskSerialStatus.serialMatches || !diskSerialStatus.driveLetter) {
    throw new Error('Expected removable drive was not found.');
  }
  return diskSerialStatus.mediaRoot;
}

async function getRootRealPath() {
  const mediaRoot = await getCurrentMediaRoot();
  if (rootRealPathCache && rootRealPathCacheSource === mediaRoot) return rootRealPathCache;
  rootRealPathCache = await realpath(mediaRoot);
  rootRealPathCacheSource = mediaRoot;
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

async function resolveWritableFolder(relativePath = '', newFolderName = '') {
  const parent = await resolveInsideMediaRoot(relativePath);
  const parentStat = await stat(parent);
  if (!parentStat.isDirectory()) {
    throw new Error('Upload target is not a folder.');
  }

  const safeNewFolderName = sanitizeFolderName(newFolderName);
  if (newFolderName && !safeNewFolderName) {
    throw new Error('Invalid new folder name.');
  }

  const target = safeNewFolderName ? path.resolve(parent, safeNewFolderName) : parent;
  const root = await getRootRealPath();
  const rootWithSeparator = root.endsWith(path.sep) ? root : `${root}${path.sep}`;
  const lowerTarget = target.toLowerCase();
  const lowerRoot = root.toLowerCase();
  const lowerRootWithSeparator = rootWithSeparator.toLowerCase();

  if (lowerTarget !== lowerRoot && !lowerTarget.startsWith(lowerRootWithSeparator)) {
    throw new Error('Upload path escapes media root.');
  }

  await mkdir(target, { recursive: true });
  return target;
}

async function uniqueWritablePath(folderPath, fileName) {
  const extension = path.extname(fileName);
  const stem = path.basename(fileName, extension);

  for (let index = 0; index < 1000; index += 1) {
    const candidateName = index === 0 ? fileName : `${stem}-${index}${extension}`;
    const candidate = path.join(folderPath, candidateName);
    try {
      await access(candidate, constants.F_OK);
    } catch {
      return { path: candidate, name: candidateName };
    }
  }

  throw new Error('Could not choose a unique upload filename.');
}

function toRelativeUrlPath(rootPath, absolutePath) {
  return path.relative(rootPath, absolutePath).split(path.sep).join('/');
}

function isMediaFile(fileName) {
  const extension = path.extname(fileName).toLowerCase();
  if (HIDDEN_MEDIA_EXTENSIONS.has(extension)) return null;
  if (IMAGE_EXTENSIONS.has(extension)) return 'image';
  if (VIDEO_EXTENSIONS.has(extension)) return 'video';
  return null;
}

function mediaDescriptor(entryName, relativePath, type, fileStat) {
  return {
    name: entryName,
    path: relativePath,
    type,
    size: fileStat.size,
    modifiedAt: fileStat.mtime.toISOString(),
    viewUrl: `/api/file?path=${encodeURIComponent(relativePath)}`,
    thumbUrl: type === 'image' ? `/api/thumbnail?path=${encodeURIComponent(relativePath)}&w=480` : undefined,
    downloadUrl: `/api/download?path=${encodeURIComponent(relativePath)}`
  };
}

async function pathExists(filePath) {
  try {
    await access(filePath, constants.F_OK);
    return true;
  } catch {
    return false;
  }
}

function runPowerShell(command) {
  return new Promise((resolve) => {
    const child = spawn('powershell.exe', ['-NoProfile', '-Command', command], { windowsHide: true });
    let output = '';

    child.stdout.on('data', (data) => {
      output += data.toString('utf8');
    });

    child.on('error', () => resolve(''));
    child.on('close', () => resolve(output.trim()));
  });
}

async function getDiskSerialStatus(forceRefresh = false) {
  const now = Date.now();
  if (!forceRefresh && diskSerialStatusCache && now - diskSerialStatusCacheAt < DISK_STATUS_CACHE_MS) {
    return diskSerialStatusCache;
  }

  const command = [
    '$ErrorActionPreference = "Stop";',
    '$disks = Get-Disk | ForEach-Object {',
    '  $disk = $_;',
    '  $letters = @();',
    '  try { $letters = @(Get-Partition -DiskNumber $disk.Number -ErrorAction Stop | Where-Object { $_.DriveLetter } | ForEach-Object { [string]$_.DriveLetter }) } catch {}',
    '  [pscustomobject]@{',
    '    Number = $disk.Number;',
    '    FriendlyName = [string]$disk.FriendlyName;',
    '    SerialNumber = [string]$disk.SerialNumber;',
    '    BusType = [string]$disk.BusType;',
    '    DriveLetters = @($letters)',
    '  }',
    '};',
    '[pscustomobject]@{ Disks = @($disks) } | ConvertTo-Json -Depth 5 -Compress'
  ].join(' ');

  const output = await runPowerShell(command);
  if (!output) {
    diskSerialStatusCache = { driveDiskSerial: '', connectedSerials: [], serialMatches: false, driveLetter: '', mediaRoot: '' };
    diskSerialStatusCacheAt = now;
    return diskSerialStatusCache;
  }

  try {
    const data = JSON.parse(output);
    const disks = Array.isArray(data.Disks) ? data.Disks : data.Disks ? [data.Disks] : [];
    const connectedSerials = [];
    let matchedDisk = null;

    for (const disk of disks) {
      const serial = normalizeDiskSerial(disk.SerialNumber || '');
      if (serial) connectedSerials.push(serial);
      if (EXPECTED_DISK_SERIAL && serial === EXPECTED_DISK_SERIAL) {
        matchedDisk = disk;
      }
    }

    const driveDiskSerial = matchedDisk ? normalizeDiskSerial(matchedDisk.SerialNumber || '') : '';
    const driveLetters = matchedDisk
      ? (Array.isArray(matchedDisk.DriveLetters) ? matchedDisk.DriveLetters : matchedDisk.DriveLetters ? [matchedDisk.DriveLetters] : [])
      : [];
    const driveLetter = String(driveLetters.find(Boolean) || '').replace(/[^A-Za-z]/g, '').slice(0, 1).toUpperCase();
    const serialMatches = Boolean(EXPECTED_DISK_SERIAL && matchedDisk && driveLetter);
    const mediaRoot = serialMatches ? mediaRootFromDriveLetter(driveLetter) : '';

    diskSerialStatusCache = { driveDiskSerial, connectedSerials, serialMatches, driveLetter, mediaRoot };
    diskSerialStatusCacheAt = now;
    return diskSerialStatusCache;
  } catch {
    diskSerialStatusCache = { driveDiskSerial: '', connectedSerials: [], serialMatches: false, driveLetter: '', mediaRoot: '' };
    diskSerialStatusCacheAt = now;
    return diskSerialStatusCache;
  }
}

async function getStatus() {
  const diskSerialStatus = await getDiskSerialStatus(true);
  const driveRoot = diskSerialStatus.driveLetter ? `${diskSerialStatus.driveLetter}:\\` : '';
  const [driveRootPresent, rootPresent] = await Promise.all([
    driveRoot ? pathExists(driveRoot) : false,
    diskSerialStatus.mediaRoot ? pathExists(diskSerialStatus.mediaRoot) : false
  ]);

  if (!rootPresent) {
    rootRealPathCache = null;
    rootRealPathCacheSource = '';
  }

  return {
    ok: true,
    computerOnline: true,
    driveLetter: diskSerialStatus.driveLetter,
    drivePresent: diskSerialStatus.serialMatches,
    driveRootPresent,
    expectedDiskSerial: EXPECTED_DISK_SERIAL,
    diskSerial: diskSerialStatus.driveDiskSerial,
    diskSerialMatches: diskSerialStatus.serialMatches,
    mediaRoot: diskSerialStatus.mediaRoot,
    mediaRootPresent: rootPresent
  };
}

async function listFolders(folderPath = '') {
  const root = await getRootRealPath();
  const folder = await resolveInsideMediaRoot(folderPath);
  const folderStat = await stat(folder);
  if (!folderStat.isDirectory()) {
    throw new Error('The requested path is not a folder.');
  }

  const folders = [];
  const entries = await readdir(folder, { withFileTypes: true });
  const childDirectories = entries
    .filter((entry) => entry.isDirectory())
    .sort((a, b) => a.name.localeCompare(b.name, 'zh-Hans-CN'));

  for (const entry of childDirectories) {
    const absolutePath = path.join(folder, entry.name);
    const relativePath = toRelativeUrlPath(root, absolutePath);
    folders.push({
      name: entry.name,
      path: relativePath,
      depth: relativePath ? relativePath.split('/').length - 1 : 0
    });
  }

  return folders;
}

async function listUploadFolders() {
  const root = await getRootRealPath();
  const folders = [];
  const stack = [{ absolutePath: root, depth: 0 }];
  const maxFolders = 5000;

  while (stack.length && folders.length < maxFolders) {
    const current = stack.pop();
    let entries = [];

    try {
      entries = await readdir(current.absolutePath, { withFileTypes: true });
    } catch {
      continue;
    }

    const childDirectories = entries
      .filter((entry) => entry.isDirectory())
      .sort((a, b) => b.name.localeCompare(a.name, 'zh-Hans-CN'));

    for (const entry of childDirectories) {
      const absolutePath = path.join(current.absolutePath, entry.name);
      const relativePath = toRelativeUrlPath(root, absolutePath);
      folders.push({
        name: entry.name,
        path: relativePath,
        depth: current.depth + 1
      });

      if (folders.length >= maxFolders) break;
      stack.push({ absolutePath, depth: current.depth + 1 });
    }
  }

  folders.sort((a, b) => a.path.localeCompare(b.path, 'zh-Hans-CN'));
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
    files.push(mediaDescriptor(entry.name, relativePath, type, fileStat));
  }

  folders.sort((a, b) => a.name.localeCompare(b.name, 'zh-Hans-CN'));
  files.sort((a, b) => a.name.localeCompare(b.name, 'zh-Hans-CN'));

  return { folders, files };
}

function randomLimit(value) {
  const limit = Number(value || 80);
  if (!Number.isFinite(limit)) return 80;
  return Math.min(Math.max(Math.round(limit), 12), 240);
}

async function randomPhotos(limitValue) {
  const root = await getRootRealPath();
  const limit = randomLimit(limitValue);
  const sample = [];
  let seen = 0;
  const stack = [root];

  while (stack.length) {
    const current = stack.pop();
    let entries = [];

    try {
      entries = await readdir(current, { withFileTypes: true });
    } catch {
      continue;
    }

    for (const entry of entries) {
      const absolutePath = path.join(current, entry.name);

      if (entry.isDirectory()) {
        stack.push(absolutePath);
        continue;
      }

      const extension = path.extname(entry.name).toLowerCase();
      if (!entry.isFile() || HIDDEN_MEDIA_EXTENSIONS.has(extension) || !IMAGE_EXTENSIONS.has(extension)) {
        continue;
      }

      let fileStat;
      try {
        fileStat = await stat(absolutePath);
      } catch {
        continue;
      }

      const relativePath = toRelativeUrlPath(root, absolutePath);
      const item = mediaDescriptor(entry.name, relativePath, 'image', fileStat);
      seen += 1;

      if (sample.length < limit) {
        sample.push(item);
      } else {
        const replacementIndex = Math.floor(Math.random() * seen);
        if (replacementIndex < limit) {
          sample[replacementIndex] = item;
        }
      }
    }
  }

  for (let index = sample.length - 1; index > 0; index -= 1) {
    const swapIndex = Math.floor(Math.random() * (index + 1));
    [sample[index], sample[swapIndex]] = [sample[swapIndex], sample[index]];
  }

  return { files: sample, totalPhotos: seen };
}

function thumbnailWidth(value) {
  const width = Number(value || 480);
  if (!Number.isFinite(width)) return 480;
  return Math.min(Math.max(Math.round(width), 180), 960);
}

function thumbnailCachePath(relativePath, fileStat, width) {
  const key = JSON.stringify({
    relativePath,
    size: fileStat.size,
    mtimeMs: Math.round(fileStat.mtimeMs),
    width
  });
  const hash = createHash('sha256').update(key).digest('hex');
  return path.join(THUMB_CACHE_ROOT, `${hash}.webp`);
}

async function streamThumbnail(request, response, relativePath, widthValue) {
  const filePath = await resolveInsideMediaRoot(relativePath);
  const fileStat = await stat(filePath);

  if (!fileStat.isFile() || isMediaFile(path.basename(filePath)) !== 'image') {
    sendError(response, 404, 'Image not found.');
    return;
  }

  const width = thumbnailWidth(widthValue);
  const cachePath = thumbnailCachePath(relativePath, fileStat, width);

  try {
    await access(cachePath, constants.F_OK);
  } catch {
    await mkdir(THUMB_CACHE_ROOT, { recursive: true });
    try {
      await sharp(filePath, { failOn: 'none', pages: 1 })
        .rotate()
        .resize({
          width,
          height: Math.round(width * 1.45),
          fit: 'inside',
          withoutEnlargement: true
        })
        .webp({ quality: 46, effort: 2 })
        .toFile(cachePath);
    } catch {
      await streamFile(request, response, relativePath, false);
      return;
    }
  }

  const cacheStat = await stat(cachePath);
  writeCors(response);
  response.writeHead(200, {
    'Content-Type': 'image/webp',
    'Content-Length': cacheStat.size,
    'Cache-Control': 'private, max-age=604800, immutable'
  });

  if (request.method === 'HEAD') {
    response.end();
    return;
  }

  createReadStream(cachePath).pipe(response);
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

async function handleUpload(request, response, requestUrl) {
  const suppliedPassword = request.headers['x-upload-password'];
  if (!UPLOAD_PASSWORD) {
    sendError(response, 503, 'Upload password is not configured.');
    return;
  }

  if (!uploadPasswordMatches(Array.isArray(suppliedPassword) ? suppliedPassword[0] : suppliedPassword)) {
    sendError(response, 401, 'Invalid upload password.');
    return;
  }

  const status = await getStatus();
  if (!status.diskSerialMatches || !status.mediaRootPresent) {
    sendError(response, 409, 'Upload target drive is not ready.');
    return;
  }

  const uploadFolder = await resolveWritableFolder(
    requestUrl.searchParams.get('folder') || '',
    requestUrl.searchParams.get('newFolder') || ''
  );
  const uploaded = [];
  const pendingWrites = [];
  let uploadError = null;

  const busboy = Busboy({
    headers: request.headers,
    limits: {
      files: 100,
      fileSize: MAX_UPLOAD_BYTES
    }
  });

  const completion = new Promise((resolve, reject) => {
    busboy.on('file', (fieldName, file, info) => {
      const safeFileName = sanitizeFileName(info.filename || '');
      const type = isMediaFile(safeFileName);

      if (!type) {
        uploadError = new Error('Only photo and video files can be uploaded.');
        file.resume();
        return;
      }

      file.pause();
      const writeTask = (async () => {
        const finalTarget = await uniqueWritablePath(uploadFolder, safeFileName);
        const temporaryPath = `${finalTarget.path}.${Date.now()}.uploading`;
        const writer = createWriteStream(temporaryPath, { flags: 'wx' });

        await new Promise((resolveWrite, rejectWrite) => {
          file.on('limit', () => {
            rejectWrite(new Error('Uploaded file is too large.'));
          });
          file.on('error', rejectWrite);
          writer.on('error', rejectWrite);
          writer.on('finish', resolveWrite);
          file.pipe(writer);
          file.resume();
        });

        await rename(temporaryPath, finalTarget.path);
        const fileStat = await stat(finalTarget.path);
        const root = await getRootRealPath();
        uploaded.push(mediaDescriptor(finalTarget.name, toRelativeUrlPath(root, finalTarget.path), type, fileStat));
      })().catch((error) => {
        uploadError = error;
        file.resume();
      });

      pendingWrites.push(writeTask);
    });

    busboy.on('filesLimit', () => {
      uploadError = new Error('Too many files in one upload.');
    });
    busboy.on('error', reject);
    busboy.on('close', async () => {
      try {
        await Promise.all(pendingWrites);
        if (uploadError) {
          reject(uploadError);
          return;
        }
        resolve();
      } catch (error) {
        reject(error);
      }
    });
  });

  request.pipe(busboy);
  await completion;

  if (!uploaded.length) {
    sendError(response, 400, 'No upload files were received.');
    return;
  }

  sendJson(response, 200, { ok: true, uploaded, count: uploaded.length });
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
      'Content-Length': targetStat.size,
      'Cache-Control': 'no-store'
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
      'Content-Length': fallbackStat.size,
      'Cache-Control': 'no-store'
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

  if (requestUrl.pathname === '/api/upload') {
    if (request.method !== 'POST') {
      sendError(response, 405, 'Method not allowed.');
      return;
    }

    try {
      await handleUpload(request, response, requestUrl);
    } catch (error) {
      const message = error instanceof Error ? error.message : 'Upload failed.';
      sendError(response, 400, 'Upload failed.', message);
    }
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
      const folder = requestUrl.searchParams.get('folder') || '';
      const status = await getStatus();
      if (!status.mediaRootPresent) {
        sendJson(response, 200, { ok: true, folders: [], status });
        return;
      }
      sendJson(response, 200, { ok: true, folder, folders: await listFolders(folder), status });
      return;
    }

    if (requestUrl.pathname === '/api/upload-folders') {
      const status = await getStatus();
      if (!status.mediaRootPresent) {
        sendJson(response, 200, { ok: true, folders: [], status });
        return;
      }
      sendJson(response, 200, { ok: true, folders: await listUploadFolders(), status });
      return;
    }

    if (requestUrl.pathname === '/api/media') {
      const folder = requestUrl.searchParams.get('folder') || '';
      const status = await getStatus();
      if (!status.mediaRootPresent) {
        sendJson(response, 200, { ok: true, folder, folders: [], files: [], status });
        return;
      }
      sendJson(response, 200, { ok: true, folder, status, ...(await listMedia(folder)) });
      return;
    }

    if (requestUrl.pathname === '/api/random') {
      const status = await getStatus();
      if (!status.mediaRootPresent) {
        sendJson(response, 200, { ok: true, mode: 'random', files: [], totalPhotos: 0, status });
        return;
      }
      sendJson(response, 200, { ok: true, mode: 'random', status, ...(await randomPhotos(requestUrl.searchParams.get('limit'))) });
      return;
    }

    if (requestUrl.pathname === '/api/thumbnail') {
      const filePath = requestUrl.searchParams.get('path');
      if (!filePath) {
        sendError(response, 400, 'Missing file path.');
        return;
      }
      await streamThumbnail(request, response, filePath, requestUrl.searchParams.get('w'));
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
  console.log(`Media folder: ${MEDIA_FOLDER_NAME}`);
  console.log(`Expected disk serial: ${EXPECTED_DISK_SERIAL || '(not configured)'}`);
});

process.on('SIGINT', () => {
  server.close(() => process.exit(0));
});
