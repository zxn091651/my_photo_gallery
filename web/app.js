const CONFIG = window.GALLERY_CONFIG || {};
const API_STORAGE_KEY = 'photo-gallery-api-base';
const queryParams = new URLSearchParams(window.location.search);
const isHostedFrontend = window.location.hostname.endsWith('github.io');
const configuredApiBase = String(CONFIG.apiBase || '').trim().replace(/\/+$/, '');
const queryApiBase = String(queryParams.get('api') || '').trim().replace(/\/+$/, '');
const storedApiBase = String(localStorage.getItem(API_STORAGE_KEY) || '').trim().replace(/\/+$/, '');

function isLocalBackendUrl(value) {
  return /^https?:\/\/(127\.0\.0\.1|localhost)(:\d+)?/i.test(value);
}

const safeStoredApiBase = isHostedFrontend && isLocalBackendUrl(storedApiBase) ? '' : storedApiBase;
const DEFAULT_API_BASE = isHostedFrontend ? '' : window.location.origin;
const isHttpsPage = window.location.protocol === 'https:';

const state = {
  apiBase: queryApiBase || configuredApiBase || safeStoredApiBase || DEFAULT_API_BASE,
  folders: [],
  currentFolder: ''
};

const elements = {
  statusText: document.querySelector('#statusText'),
  apiInput: document.querySelector('#apiInput'),
  saveApiButton: document.querySelector('#saveApiButton'),
  refreshButton: document.querySelector('#refreshButton'),
  rootButton: document.querySelector('#rootButton'),
  folderCount: document.querySelector('#folderCount'),
  folderList: document.querySelector('#folderList'),
  childFolderList: document.querySelector('#childFolderList'),
  currentFolder: document.querySelector('#currentFolder'),
  mediaCount: document.querySelector('#mediaCount'),
  mediaGrid: document.querySelector('#mediaGrid'),
  viewerDialog: document.querySelector('#viewerDialog'),
  viewerTitle: document.querySelector('#viewerTitle'),
  viewerStage: document.querySelector('#viewerStage'),
  downloadLink: document.querySelector('#downloadLink'),
  closeViewerButton: document.querySelector('#closeViewerButton')
};

elements.apiInput.value = state.apiBase;
elements.apiInput.placeholder = isHostedFrontend
  ? 'https://你的公网后端地址'
  : window.location.origin;

function apiUrl(path, params = {}) {
  if (!state.apiBase) {
    throw new Error('请先填写公网后端地址。');
  }

  if (isHttpsPage && state.apiBase.startsWith('http://')) {
    throw new Error('当前页面是 HTTPS，不能连接 HTTP 后端。请直接打开 http://photo.fucku.top，或把隧道配置为 HTTPS。');
  }

  const url = new URL(path, state.apiBase);
  for (const [key, value] of Object.entries(params)) {
    if (value !== undefined && value !== null) {
      url.searchParams.set(key, value);
    }
  }
  return url;
}

async function requestJson(path, params) {
  const response = await fetch(apiUrl(path, params));
  if (!response.ok) {
    throw new Error(`HTTP ${response.status}`);
  }
  return response.json();
}

function formatBytes(value) {
  if (!Number.isFinite(value)) return '';
  const units = ['B', 'KB', 'MB', 'GB', 'TB'];
  let size = value;
  let index = 0;
  while (size >= 1024 && index < units.length - 1) {
    size /= 1024;
    index += 1;
  }
  return `${size.toFixed(index === 0 ? 0 : 1)} ${units[index]}`;
}

function setStatus(status) {
  if (!status) {
    elements.statusText.textContent = isHostedFrontend
      ? '请填写公网后端地址，并确认电脑、图库后端和内网穿透隧道都已运行。'
      : '无法连接后端。请确认电脑已开机，并已运行 npm run start。';
    elements.statusText.className = 'error';
    return;
  }

  elements.statusText.className = '';

  if (!status.drivePresent) {
    elements.statusText.textContent = `${status.driveLetter}: 盘未检测到。请插入移动硬盘 ${status.expectedVolume}。`;
    return;
  }

  if (!status.mediaRootPresent) {
    elements.statusText.textContent = `已检测到 ${status.driveLetter}: 盘，但没有找到 ${status.mediaRoot}。`;
    return;
  }

  const volume = status.volumeName ? `，卷标 ${status.volumeName}` : '';
  elements.statusText.textContent = `本机后端在线，${status.driveLetter}: 盘已连接${volume}。`;
}

function renderFolders() {
  elements.folderCount.textContent = String(state.folders.length);
  elements.folderList.replaceChildren();

  if (!state.folders.length) {
    const empty = document.createElement('p');
    empty.className = 'empty';
    empty.textContent = '当前目录下没有子文件夹。';
    elements.folderList.append(empty);
    return;
  }

  for (const folder of state.folders) {
    const button = document.createElement('button');
    button.type = 'button';
    button.className = `folder-item${folder.path === state.currentFolder ? ' active' : ''}`;
    button.textContent = folder.name;
    button.title = folder.path;
    button.addEventListener('click', () => openFolder(folder.path));
    elements.folderList.append(button);
  }
}

function renderChildFolders(folders) {
  elements.childFolderList.replaceChildren();

  for (const folder of folders) {
    const button = document.createElement('button');
    button.type = 'button';
    button.className = 'child-folder';
    button.textContent = folder.name;
    button.title = folder.path;
    button.addEventListener('click', () => openFolder(folder.path));
    elements.childFolderList.append(button);
  }
}

function renderMedia(files) {
  elements.mediaGrid.replaceChildren();

  if (!files.length) {
    const empty = document.createElement('p');
    empty.className = 'empty';
    empty.textContent = '这个文件夹里没有可预览的照片或视频。';
    elements.mediaGrid.append(empty);
    return;
  }

  for (const file of files) {
    const card = document.createElement('article');
    card.className = 'media-card';

    const trigger = document.createElement('button');
    trigger.type = 'button';
    trigger.className = 'thumb-button';
    trigger.title = file.name;
    trigger.style.padding = '0';
    trigger.style.border = '0';
    trigger.style.width = '100%';
    trigger.addEventListener('click', () => openViewer(file));

    const source = apiUrl(file.viewUrl).toString();

    if (file.type === 'image') {
      const image = document.createElement('img');
      image.className = 'thumb';
      image.loading = 'lazy';
      image.alt = file.name;
      image.src = source;
      trigger.append(image);
    } else {
      const video = document.createElement('video');
      video.className = 'thumb video-thumb';
      video.preload = 'metadata';
      video.muted = true;
      video.src = `${source}#t=0.1`;
      trigger.append(video);
    }

    const info = document.createElement('div');
    info.className = 'media-info';

    const name = document.createElement('span');
    name.className = 'media-name';
    name.textContent = file.name;

    const meta = document.createElement('div');
    meta.className = 'media-meta';
    meta.innerHTML = `<span>${file.type === 'image' ? '照片' : '视频'}</span><span>${formatBytes(file.size)}</span>`;

    info.append(name, meta);
    card.append(trigger, info);
    elements.mediaGrid.append(card);
  }
}

async function openFolder(folderPath = '') {
  state.currentFolder = folderPath;
  elements.currentFolder.textContent = folderPath || '影像备份';
  elements.statusText.textContent = '正在加载目录...';
  elements.mediaCount.textContent = '正在加载照片和视频...';

  try {
    const data = await requestJson('/api/media', { folder: folderPath });
    setStatus(data.status);
    state.folders = data.folders || [];
    renderFolders();
    renderChildFolders(state.folders);
    renderMedia(data.files || []);
    elements.mediaCount.textContent = `${state.folders.length} 个子文件夹，${data.files?.length || 0} 个照片/视频`;
  } catch (error) {
    const message = error instanceof Error ? error.message : '';
    elements.statusText.textContent = message === 'Failed to fetch'
      ? '无法连接后端，或当前目录加载超时。请确认电脑、后端和内网穿透隧道都已运行。'
      : message || '加载失败。';
    elements.statusText.className = 'error';
    state.folders = [];
    renderFolders();
    renderChildFolders([]);
    renderMedia([]);
    elements.currentFolder.textContent = '无法连接';
    elements.mediaCount.textContent = '请检查后端地址、隧道和当前目录。';
  }
}

function openViewer(file) {
  elements.viewerTitle.textContent = file.name;
  elements.viewerStage.replaceChildren();

  const source = apiUrl(file.viewUrl).toString();
  const downloadUrl = apiUrl(file.downloadUrl).toString();
  elements.downloadLink.href = downloadUrl;
  elements.downloadLink.setAttribute('download', file.name);

  if (file.type === 'image') {
    const image = document.createElement('img');
    image.alt = file.name;
    image.src = source;
    elements.viewerStage.append(image);
  } else {
    const video = document.createElement('video');
    video.controls = true;
    video.autoplay = true;
    video.src = source;
    elements.viewerStage.append(video);
  }

  elements.viewerDialog.showModal();
}

function refreshAll() {
  openFolder(state.currentFolder);
}

elements.saveApiButton.addEventListener('click', () => {
  state.apiBase = elements.apiInput.value.trim().replace(/\/+$/, '') || DEFAULT_API_BASE;
  if (state.apiBase) {
    localStorage.setItem(API_STORAGE_KEY, state.apiBase);
  } else {
    localStorage.removeItem(API_STORAGE_KEY);
  }
  elements.apiInput.value = state.apiBase;
  refreshAll();
});

elements.refreshButton.addEventListener('click', refreshAll);
elements.rootButton.addEventListener('click', () => openFolder(''));
elements.closeViewerButton.addEventListener('click', () => {
  elements.viewerDialog.close();
  elements.viewerStage.replaceChildren();
});

elements.viewerDialog.addEventListener('close', () => {
  elements.viewerStage.replaceChildren();
});

refreshAll();
