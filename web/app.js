const DEFAULT_API_BASE = 'http://127.0.0.1:8787';
const API_STORAGE_KEY = 'photo-gallery-api-base';

const state = {
  apiBase: localStorage.getItem(API_STORAGE_KEY) || DEFAULT_API_BASE,
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

function apiUrl(path, params = {}) {
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
    elements.statusText.textContent = '无法连接本机后端。请确认电脑已开机，并已运行 npm run start。';
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
    empty.textContent = '没有找到子文件夹。';
    elements.folderList.append(empty);
    return;
  }

  for (const folder of state.folders) {
    const button = document.createElement('button');
    button.type = 'button';
    button.className = `folder-item${folder.path === state.currentFolder ? ' active' : ''}`;
    button.style.paddingLeft = `${12 + Math.min(folder.depth, 8) * 14}px`;
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

async function loadFolders() {
  elements.statusText.textContent = '正在扫描文件夹...';
  const data = await requestJson('/api/folders');
  setStatus(data.status);
  state.folders = data.folders || [];
  renderFolders();
}

async function openFolder(folderPath = '') {
  state.currentFolder = folderPath;
  elements.currentFolder.textContent = folderPath || '影像备份';
  elements.mediaCount.textContent = '正在加载媒体...';
  renderFolders();

  try {
    const data = await requestJson('/api/media', { folder: folderPath });
    renderChildFolders(data.folders || []);
    renderMedia(data.files || []);
    elements.mediaCount.textContent = `${data.folders?.length || 0} 个子文件夹，${data.files?.length || 0} 个照片/视频`;
  } catch (error) {
    elements.mediaCount.textContent = '加载失败';
    elements.mediaGrid.innerHTML = `<p class="error">${error.message}</p>`;
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

async function refreshAll() {
  try {
    await loadFolders();
    await openFolder(state.currentFolder);
  } catch {
    setStatus(null);
    state.folders = [];
    renderFolders();
    renderChildFolders([]);
    renderMedia([]);
    elements.currentFolder.textContent = '无法连接';
    elements.mediaCount.textContent = '请检查后端地址和本机服务。';
  }
}

elements.saveApiButton.addEventListener('click', () => {
  state.apiBase = elements.apiInput.value.trim().replace(/\/+$/, '') || DEFAULT_API_BASE;
  localStorage.setItem(API_STORAGE_KEY, state.apiBase);
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
