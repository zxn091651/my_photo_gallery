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
  isRandomMode: false,
  mediaFiles: [],
  activeMediaIndex: 0,
  drag: null
};

const elements = {
  statusText: document.querySelector('#statusText'),
  uploadButton: document.querySelector('#uploadButton'),
  rootButton: document.querySelector('#rootButton'),
  currentFolder: document.querySelector('#currentFolder'),
  mediaCount: document.querySelector('#mediaCount'),
  mediaGrid: document.querySelector('#mediaGrid'),
  viewerDialog: document.querySelector('#viewerDialog'),
  viewerTitle: document.querySelector('#viewerTitle'),
  viewerStage: document.querySelector('#viewerStage'),
  downloadLink: document.querySelector('#downloadLink'),
  closeViewerButton: document.querySelector('#closeViewerButton'),
  uploadDialog: document.querySelector('#uploadDialog'),
  uploadForm: document.querySelector('#uploadForm'),
  closeUploadButton: document.querySelector('#closeUploadButton'),
  uploadFolderInput: document.querySelector('#uploadFolderInput'),
  newFolderInput: document.querySelector('#newFolderInput'),
  uploadPasswordInput: document.querySelector('#uploadPasswordInput'),
  uploadFilesInput: document.querySelector('#uploadFilesInput'),
  uploadStatus: document.querySelector('#uploadStatus'),
  submitUploadButton: document.querySelector('#submitUploadButton')
};

function apiUrl(path, params = {}) {
  if (!state.apiBase) {
    throw new Error('没有配置后端地址。请直接打开 http://photo.fucku.top。');
  }

  if (isHttpsPage && state.apiBase.startsWith('http://')) {
    throw new Error('当前页面是 HTTPS，不能连接 HTTP 后端。请直接打开 http://photo.fucku.top。');
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

async function uploadFiles() {
  const files = Array.from(elements.uploadFilesInput.files || []);
  const password = elements.uploadPasswordInput.value;
  const folder = elements.uploadFolderInput.value.trim().replace(/\\/g, '/').replace(/^\/+/, '');
  const newFolder = elements.newFolderInput.value.trim();

  if (!files.length) {
    elements.uploadStatus.textContent = '请选择要上传的照片或视频。';
    return;
  }

  elements.submitUploadButton.disabled = true;
  elements.uploadStatus.textContent = '正在上传...';

  try {
    const formData = new FormData();
    for (const file of files) {
      formData.append('files', file, file.name);
    }

    const response = await fetch(apiUrl('/api/upload', { folder, newFolder }), {
      method: 'POST',
      headers: {
        'X-Upload-Password': password
      },
      body: formData
    });

    const data = await response.json().catch(() => ({}));
    if (!response.ok || !data.ok) {
      throw new Error(data.details || data.error || `HTTP ${response.status}`);
    }

    elements.uploadStatus.textContent = `上传完成：${data.count} 个文件。`;
    elements.uploadPasswordInput.value = '';
    elements.uploadFilesInput.value = '';
    await openFolder(state.currentFolder);
  } catch (error) {
    elements.uploadStatus.textContent = error instanceof Error ? error.message : '上传失败。';
  } finally {
    elements.submitUploadButton.disabled = false;
  }
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

function setConnectionStatus(isHealthy, detail = '') {
  elements.statusText.className = `connection-status ${isHealthy ? 'is-good' : 'is-bad'}`;
  elements.statusText.title = detail;
  elements.statusText.setAttribute('aria-label', `后端连接状态：${isHealthy ? '正常' : '异常'}`);
  elements.statusText.replaceChildren();

  const dot = document.createElement('span');
  dot.className = 'status-dot';
  dot.setAttribute('aria-hidden', 'true');

  const label = document.createElement('span');
  label.textContent = '后端连接状态';

  elements.statusText.append(dot, label);
}

function setStatus(status) {
  if (!status) {
    setConnectionStatus(false, '无法读取后端状态。');
    return;
  }

  const isHealthy = Boolean(status.diskSerialMatches && status.mediaRootPresent);
  const detail = isHealthy
    ? `正常工作：已检测到指定移动硬盘序列号。`
    : `异常：未检测到指定移动硬盘序列号，或影像备份目录未就绪。`;
  setConnectionStatus(isHealthy, detail);
}

function folderTitle(folderPath) {
  return folderPath || '影像备份';
}

function createFolderButton(folder) {
  const button = document.createElement('button');
  button.type = 'button';
  button.className = 'folder-card';
  button.title = folder.path;

  const name = document.createElement('strong');
  name.textContent = folder.name;

  const meta = document.createElement('span');
  meta.textContent = '打开相册';

  button.append(name, meta);
  button.addEventListener('click', () => openFolder(folder.path));
  return button;
}

function createRandomButton() {
  const button = document.createElement('button');
  button.type = 'button';
  button.className = 'folder-card random-card';
  button.title = '随机探索所有子文件夹的照片';

  const name = document.createElement('strong');
  name.textContent = '随机探索';

  const meta = document.createElement('span');
  meta.textContent = '随机照片';

  button.append(name, meta);
  button.addEventListener('click', openRandomMode);
  return button;
}

function renderFolderGallery(folders) {
  elements.mediaGrid.replaceChildren();

  if (!folders.length) {
    const empty = document.createElement('p');
    empty.className = 'empty';
    empty.textContent = '这里暂时没有可显示的照片、视频或文件夹。';
    elements.mediaGrid.append(empty);
    return;
  }

  const gallery = document.createElement('section');
  gallery.className = 'folder-gallery';
  gallery.setAttribute('aria-label', '文件夹');
  gallery.append(createRandomButton());

  for (const folder of folders) {
    gallery.append(createFolderButton(folder));
  }

  elements.mediaGrid.append(gallery);
}

function renderMedia(files, folders) {
  state.mediaFiles = files || [];
  state.activeMediaIndex = 0;

  if (!state.mediaFiles.length) {
    renderFolderGallery(folders || []);
    return;
  }

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
  const source = apiUrl(file.thumbUrl || file.viewUrl).toString();

  if (file.type === 'image') {
    const image = document.createElement('img');
    image.className = 'deck-media';
    image.loading = isTopCard ? 'eager' : 'lazy';
    image.alt = file.name;
    image.src = source;
    image.decoding = 'async';
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
    card.dataset.swipeLabel = deltaX < 0 ? '下一张' : '上一张';
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
      setTimeout(() => navigateMedia(deltaX < 0 ? 1 : -1), 180);
      return;
    }

    card.style.transform = '';
    card.style.removeProperty('--swipe-progress');
    delete card.dataset.swipeLabel;
    if (!moved) {
      openViewer(currentMediaFile());
    }
  });

  card.addEventListener('pointercancel', () => {
    state.drag = null;
    card.classList.remove('dragging');
    card.style.transform = '';
    card.style.removeProperty('--swipe-progress');
    delete card.dataset.swipeLabel;
  });
}

function preloadUpcomingThumbnails() {
  const upcomingFiles = state.mediaFiles.slice(state.activeMediaIndex + 1, state.activeMediaIndex + 3);
  for (const file of upcomingFiles) {
    if (file.type !== 'image' || !file.thumbUrl) continue;
    const image = new Image();
    image.decoding = 'async';
    image.src = apiUrl(file.thumbUrl).toString();
  }
}

function renderMediaDeck() {
  elements.mediaGrid.replaceChildren();

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

  const downloadLink = document.createElement('a');
  downloadLink.className = 'button-link';
  downloadLink.textContent = '下载';
  downloadLink.href = apiUrl(currentMediaFile().downloadUrl).toString();
  downloadLink.setAttribute('download', currentMediaFile().name);

  controls.append(downloadLink);
  deckShell.append(deck, controls);
  elements.mediaGrid.append(deckShell);
  preloadUpcomingThumbnails();
}

async function openFolder(folderPath = '') {
  state.currentFolder = folderPath;
  state.isRandomMode = false;
  elements.currentFolder.textContent = folderTitle(folderPath);
  setConnectionStatus(false, '正在连接后端并读取影像备份。');
  elements.mediaCount.textContent = '正在读取文件夹...';
  elements.rootButton.hidden = !folderPath;

  try {
    const data = await requestJson('/api/media', { folder: folderPath });
    setStatus(data.status);
    state.folders = data.folders || [];
    renderMedia(data.files || [], state.folders);

    const folderText = `${state.folders.length} 个文件夹`;
    const mediaText = `${data.files?.length || 0} 个照片/视频`;
    elements.mediaCount.textContent = state.mediaFiles.length
      ? `${folderText}，${mediaText}`
      : state.folders.length
        ? '请选择一个文件夹'
        : '没有可显示的照片或视频';
  } catch (error) {
    const message = error instanceof Error ? error.message : '';
    const detail = message === 'Failed to fetch'
      ? '无法连接后端，或当前目录加载超时。请确认电脑、后端和内网穿透隧道都已运行。'
      : message || '加载失败。';
    setConnectionStatus(false, detail);
    state.folders = [];
    state.mediaFiles = [];
    renderFolderGallery([]);
    elements.currentFolder.textContent = '无法连接';
    elements.mediaCount.textContent = '请检查后端和隧道。';
  }
}

async function openRandomMode() {
  state.currentFolder = '';
  state.isRandomMode = true;
  elements.currentFolder.textContent = '随机探索';
  setConnectionStatus(false, '正在随机扫描所有子文件夹的照片。');
  elements.mediaCount.textContent = '正在随机抽取照片...';
  elements.rootButton.hidden = false;

  try {
    const data = await requestJson('/api/random', { limit: 96 });
    setStatus(data.status);
    state.folders = [];
    renderMedia(data.files || [], []);
    elements.mediaCount.textContent = data.files?.length
      ? `已随机抽取 ${data.files.length} 张照片`
      : '没有找到可随机探索的照片';
  } catch (error) {
    const message = error instanceof Error ? error.message : '';
    const detail = message === 'Failed to fetch'
      ? '随机探索加载失败。请确认后端和内网穿透隧道都已运行。'
      : message || '随机探索加载失败。';
    setConnectionStatus(false, detail);
    state.mediaFiles = [];
    renderFolderGallery(state.folders);
    elements.currentFolder.textContent = '随机探索失败';
    elements.mediaCount.textContent = '请稍后再试。';
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

function openUploadDialog() {
  elements.uploadFolderInput.value = state.isRandomMode ? '' : state.currentFolder;
  elements.newFolderInput.value = '';
  elements.uploadPasswordInput.value = '';
  elements.uploadFilesInput.value = '';
  elements.uploadStatus.textContent = '';
  elements.uploadDialog.showModal();
}

elements.uploadButton.addEventListener('click', openUploadDialog);
elements.rootButton.addEventListener('click', () => openFolder(''));
elements.closeUploadButton.addEventListener('click', () => {
  elements.uploadDialog.close();
});
elements.uploadForm.addEventListener('submit', (event) => {
  event.preventDefault();
  uploadFiles();
});
elements.closeViewerButton.addEventListener('click', () => {
  elements.viewerDialog.close();
  elements.viewerStage.replaceChildren();
});

elements.viewerDialog.addEventListener('close', () => {
  elements.viewerStage.replaceChildren();
});

document.addEventListener('keydown', (event) => {
  if (elements.viewerDialog.open || !state.mediaFiles.length) {
    return;
  }

  if (event.key === 'ArrowLeft') {
    navigateMedia(-1);
  }

  if (event.key === 'ArrowRight') {
    navigateMedia(1);
  }
});

openFolder('');
