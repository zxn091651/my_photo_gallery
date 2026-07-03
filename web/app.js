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
  currentFolder: '',
  mediaFiles: [],
  activeMediaIndex: 0,
  drag: null
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
    elements.statusText.textContent = '无法读取后端状态。请确认当前页面地址是 http://photo.fucku.top，且后端和内网穿透隧道正在运行。';
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
  state.mediaFiles = files || [];
  state.activeMediaIndex = 0;
  renderMediaDeck();
}

function currentMediaFile() {
  return state.mediaFiles[state.activeMediaIndex] || null;
}

function setActiveMediaIndex(index) {
  const maxIndex = Math.max(0, state.mediaFiles.length - 1);
  state.activeMediaIndex = Math.min(Math.max(index, 0), maxIndex);
  renderMediaDeck();
}

function navigateMedia(direction) {
  setActiveMediaIndex(state.activeMediaIndex + direction);
}

function createMediaElement(file, isTopCard) {
  const source = apiUrl(file.viewUrl).toString();

  if (file.type === 'image') {
    const image = document.createElement('img');
    image.className = 'deck-media';
    image.loading = isTopCard ? 'eager' : 'lazy';
    image.alt = file.name;
    image.src = source;
    return image;
  }

  const video = document.createElement('video');
  video.className = 'deck-media';
  video.preload = 'metadata';
  video.muted = true;
  video.playsInline = true;
  video.src = `${source}#t=0.1`;
  return video;
}

function attachSwipeHandlers(card) {
  card.addEventListener('pointerdown', (event) => {
    if (event.button !== 0) return;
    state.drag = {
      startX: event.clientX,
      startY: event.clientY,
      moved: false
    };
    card.classList.add('dragging');
    card.setPointerCapture(event.pointerId);
  });

  card.addEventListener('pointermove', (event) => {
    if (!state.drag) return;
    const deltaX = event.clientX - state.drag.startX;
    const deltaY = event.clientY - state.drag.startY;
    state.drag.moved = state.drag.moved || Math.abs(deltaX) > 6 || Math.abs(deltaY) > 6;
    const rotation = deltaX / 18;
    card.style.transform = `translate(${deltaX}px, ${deltaY}px) rotate(${rotation}deg)`;
    card.style.setProperty('--swipe-progress', String(Math.min(Math.abs(deltaX) / 160, 1)));
  });

  card.addEventListener('pointerup', (event) => {
    if (!state.drag) return;
    const deltaX = event.clientX - state.drag.startX;
    const moved = state.drag.moved;
    state.drag = null;
    card.classList.remove('dragging');

    if (Math.abs(deltaX) > 96) {
      const exitX = deltaX > 0 ? window.innerWidth : -window.innerWidth;
      card.style.transform = `translate(${exitX}px, 24px) rotate(${deltaX > 0 ? 18 : -18}deg)`;
      setTimeout(() => navigateMedia(deltaX > 0 ? 1 : -1), 180);
      return;
    }

    card.style.transform = '';
    card.style.removeProperty('--swipe-progress');
    if (!moved) {
      openViewer(currentMediaFile());
    }
  });

  card.addEventListener('pointercancel', () => {
    state.drag = null;
    card.classList.remove('dragging');
    card.style.transform = '';
    card.style.removeProperty('--swipe-progress');
  });
}

function renderMediaDeck() {
  elements.mediaGrid.replaceChildren();

  if (!state.mediaFiles.length) {
    const empty = document.createElement('p');
    empty.className = 'empty';
    empty.textContent = '这个文件夹里没有可预览的照片或视频。';
    elements.mediaGrid.append(empty);
    return;
  }

  const deckShell = document.createElement('section');
  deckShell.className = 'deck-shell';

  const deck = document.createElement('div');
  deck.className = 'deck';

  const visibleFiles = state.mediaFiles.slice(state.activeMediaIndex, state.activeMediaIndex + 4);
  [...visibleFiles].reverse().forEach((file, reverseIndex) => {
    const indexFromTop = visibleFiles.length - 1 - reverseIndex;
    const absoluteIndex = state.activeMediaIndex + indexFromTop;
    const isTopCard = indexFromTop === 0;
    const card = document.createElement('article');
    card.className = `deck-card${isTopCard ? ' active' : ''}`;
    card.style.setProperty('--stack-index', String(indexFromTop));
    card.style.zIndex = String(10 - indexFromTop);

    if (isTopCard) {
      card.tabIndex = 0;
      card.setAttribute('role', 'button');
      card.setAttribute('aria-label', `打开 ${file.name}`);
      attachSwipeHandlers(card);
      card.addEventListener('keydown', (event) => {
        if (event.key === 'Enter' || event.key === ' ') {
          event.preventDefault();
          openViewer(file);
        }
      });
    }

    const preview = document.createElement('div');
    preview.className = 'deck-preview';
    preview.append(createMediaElement(file, isTopCard));

    const label = document.createElement('div');
    label.className = 'deck-label';
    label.innerHTML = `
      <span>${file.type === 'image' ? '照片' : '视频'} · ${absoluteIndex + 1}/${state.mediaFiles.length}</span>
      <strong></strong>
      <small>${formatBytes(file.size)}</small>
    `;
    label.querySelector('strong').textContent = file.name;

    card.append(preview, label);
    deck.append(card);
  });

  const controls = document.createElement('div');
  controls.className = 'deck-controls';

  const previousButton = document.createElement('button');
  previousButton.type = 'button';
  previousButton.textContent = '上一张';
  previousButton.disabled = state.activeMediaIndex === 0;
  previousButton.addEventListener('click', () => navigateMedia(-1));

  const openButton = document.createElement('button');
  openButton.type = 'button';
  openButton.className = 'primary-control';
  openButton.textContent = '打开查看';
  openButton.addEventListener('click', () => openViewer(currentMediaFile()));

  const downloadLink = document.createElement('a');
  downloadLink.className = 'button-link';
  downloadLink.textContent = '下载';
  downloadLink.href = apiUrl(currentMediaFile().downloadUrl).toString();
  downloadLink.setAttribute('download', currentMediaFile().name);

  const nextButton = document.createElement('button');
  nextButton.type = 'button';
  nextButton.textContent = '下一张';
  nextButton.disabled = state.activeMediaIndex >= state.mediaFiles.length - 1;
  nextButton.addEventListener('click', () => navigateMedia(1));

  controls.append(previousButton, openButton, downloadLink, nextButton);
  deckShell.append(deck, controls);
  elements.mediaGrid.append(deckShell);
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
  if (!file) return;

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

document.addEventListener('keydown', (event) => {
  const target = event.target;
  if (target instanceof HTMLInputElement || elements.viewerDialog.open || !state.mediaFiles.length) {
    return;
  }

  if (event.key === 'ArrowLeft') {
    navigateMedia(-1);
  }

  if (event.key === 'ArrowRight') {
    navigateMedia(1);
  }
});

refreshAll();
