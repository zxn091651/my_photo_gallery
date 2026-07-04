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
const DEFAULT_FOLDER_PAGE_SIZE = window.matchMedia('(max-width: 520px)').matches ? 7 : 13;
const HEARTBEAT_INTERVAL_MS = 10_000;
const HEARTBEAT_FAILURES_BEFORE_BAD = 3;
const DOWNLOAD_STATUS_GRACE_MS = 120_000;
const TAP_SLOP = 2;

const state = {
  apiBase: queryApiBase || configuredApiBase || safeStoredApiBase || DEFAULT_API_BASE,
  folders: [],
  uploadFolders: [],
  folderPage: 0,
  folderPageSize: DEFAULT_FOLDER_PAGE_SIZE,
  currentFolder: '',
  isRandomMode: false,
  mediaFiles: [],
  activeMediaIndex: 0,
  deckAnimation: null,
  preloadTimer: null,
  resizeTimer: null,
  heartbeatInFlight: false,
  heartbeatFailures: 0,
  downloadStatusGraceUntil: 0,
  drag: null
};

const elements = {
  statusText: document.querySelector('#statusText'),
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
  uploadFolderSelect: document.querySelector('#uploadFolderSelect'),
  newFolderInput: document.querySelector('#newFolderInput'),
  uploadPasswordInput: document.querySelector('#uploadPasswordInput'),
  uploadFilesInput: document.querySelector('#uploadFilesInput'),
  uploadProgress: document.querySelector('#uploadProgress'),
  uploadProgressTrack: document.querySelector('#uploadProgress .upload-progress-track'),
  uploadProgressBar: document.querySelector('#uploadProgressBar'),
  uploadProgressText: document.querySelector('#uploadProgressText'),
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

function setUploadProgress(percent, isVisible = true) {
  const safePercent = Math.min(Math.max(Math.round(percent), 0), 100);
  elements.uploadProgress.hidden = !isVisible;
  elements.uploadProgressBar.style.width = `${safePercent}%`;
  elements.uploadProgressText.textContent = `${safePercent}%`;
  elements.uploadProgressTrack.setAttribute('aria-valuenow', String(safePercent));
}

function resetUploadProgress() {
  setUploadProgress(0, false);
}

function createUploadError(message, status = 0, canRetryWithoutProgress = false) {
  const error = new Error(message);
  error.status = status;
  error.canRetryWithoutProgress = canRetryWithoutProgress;
  return error;
}

function uploadFormData(url, password, formData) {
  return new Promise((resolve, reject) => {
    const request = new XMLHttpRequest();

    url.searchParams.set('password', password);
    request.open('POST', url.toString());

    request.upload.addEventListener('progress', (event) => {
      if (!event.lengthComputable) {
        elements.uploadProgress.hidden = false;
        elements.uploadStatus.textContent = '正在上传...';
        return;
      }

      const percent = Math.min(Math.round((event.loaded / event.total) * 100), 100);
      setUploadProgress(percent);
      elements.uploadStatus.textContent = percent >= 100 ? '上传完成，正在保存到硬盘...' : `正在上传 ${percent}%`;
    });

    request.addEventListener('load', () => {
      let data = {};
      try {
        data = JSON.parse(request.responseText || '{}');
      } catch {
        reject(createUploadError(`HTTP ${request.status}`, request.status, request.status === 404));
        return;
      }

      if (request.status < 200 || request.status >= 300 || !data.ok) {
        reject(createUploadError(data.details || data.error || `HTTP ${request.status}`, request.status, request.status === 404));
        return;
      }
      resolve(data);
    });

    request.addEventListener('error', () => reject(createUploadError('上传连接失败。', 0, true)));
    request.addEventListener('abort', () => reject(createUploadError('上传已取消。', 0, false)));
    request.send(formData);
  });
}

async function uploadFormDataWithoutProgress(url, password, formData) {
  url.searchParams.set('password', password);
  const response = await fetch(url.toString(), {
    method: 'POST',
    body: formData
  });
  const data = await response.json().catch(() => ({}));
  if (!response.ok || !data.ok) {
    throw new Error(data.details || data.error || `HTTP ${response.status}`);
  }
  return data;
}

async function uploadFiles() {
  const files = Array.from(elements.uploadFilesInput.files || []);
  const password = elements.uploadPasswordInput.value;
  const newFolder = elements.newFolderInput.value.trim();
  const folder = newFolder
    ? ''
    : elements.uploadFolderSelect.value.trim().replace(/\\/g, '/').replace(/^\/+/, '');

  if (!files.length) {
    elements.uploadStatus.textContent = '请选择要上传的照片或视频。';
    return;
  }

  if (!newFolder && !folder) {
    elements.uploadStatus.textContent = '请选择影像备份下的子文件夹作为上传目标。';
    return;
  }

  elements.submitUploadButton.disabled = true;
  setUploadProgress(0);
  elements.uploadStatus.textContent = '正在准备上传...';

  try {
    const formData = new FormData();
    for (const file of files) {
      formData.append('files', file, file.name);
    }

    let data;
    try {
      data = await uploadFormData(apiUrl('/api/upload', { folder, newFolder }), password, formData);
    } catch (error) {
      if (!error?.canRetryWithoutProgress) {
        throw error;
      }
      setUploadProgress(100);
      elements.uploadStatus.textContent = '进度上传不兼容，正在使用兼容模式保存...';
      data = await uploadFormDataWithoutProgress(apiUrl('/api/upload', { folder, newFolder }), password, formData);
    }

    const uploadSuccessMessage = `上传完成：${data.count} 个文件。`;
    setUploadProgress(100);
    elements.uploadStatus.textContent = uploadSuccessMessage;
    elements.uploadPasswordInput.value = '';
    elements.uploadFilesInput.value = '';
    if (newFolder) {
      elements.newFolderInput.value = '';
      await loadUploadFolders(newFolder);
      elements.uploadStatus.textContent = uploadSuccessMessage;
    }
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
  if (isHealthy) {
    state.heartbeatFailures = 0;
  }
  setConnectionStatus(isHealthy, detail);
}

function markDownloadStarting() {
  state.downloadStatusGraceUntil = Date.now() + DOWNLOAD_STATUS_GRACE_MS;
  setConnectionStatus(true, '正在开始下载，后端连接状态保持正常。');
}

async function checkBackendHeartbeat() {
  if (state.heartbeatInFlight) return;
  state.heartbeatInFlight = true;

  try {
    const status = await requestJson('/api/status');
    setStatus(status);
  } catch {
    state.heartbeatFailures += 1;
    if (Date.now() < state.downloadStatusGraceUntil) {
      setConnectionStatus(true, '下载请求进行中，暂不把心跳延迟判定为异常。');
    } else if (state.heartbeatFailures >= HEARTBEAT_FAILURES_BEFORE_BAD) {
      setConnectionStatus(false, '连续心跳检测失败：无法连接后端。');
    }
  } finally {
    state.heartbeatInFlight = false;
  }
}

function startBackendHeartbeat() {
  checkBackendHeartbeat();
  window.setInterval(checkBackendHeartbeat, HEARTBEAT_INTERVAL_MS);
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

function createUploadButton() {
  const button = document.createElement('button');
  button.type = 'button';
  button.className = 'folder-card upload-card';
  button.title = '选择文件夹并上传照片或视频';

  const name = document.createElement('strong');
  name.textContent = '上传';

  const meta = document.createElement('span');
  meta.textContent = '需要密码';

  button.append(name, meta);
  button.addEventListener('click', openUploadDialog);
  return button;
}

function pageLabel(pageIndex) {
  return String(pageIndex + 1);
}

function setFolderPage(pageIndex) {
  const pageCount = Math.max(1, Math.ceil(state.folders.length / state.folderPageSize));
  state.folderPage = Math.min(Math.max(pageIndex, 0), pageCount - 1);
  renderFolderGallery(state.folders);
}

function createPageButton(pageIndex) {
  const button = document.createElement('button');
  button.type = 'button';
  button.className = 'page-button';
  button.textContent = pageLabel(pageIndex);
  button.disabled = pageIndex === state.folderPage;
  button.addEventListener('click', () => setFolderPage(pageIndex));
  return button;
}

function createPagerArrow(label, title, targetPage, disabled, extraClass = '') {
  const button = document.createElement('button');
  button.type = 'button';
  button.className = `page-arrow${extraClass ? ` ${extraClass}` : ''}`;
  button.textContent = label;
  button.title = title;
  button.disabled = disabled;
  button.addEventListener('click', () => setFolderPage(targetPage));
  return button;
}

function renderFolderPager(pageCount) {
  if (pageCount <= 1) return null;

  const pager = document.createElement('nav');
  pager.className = 'folder-pager';
  pager.setAttribute('aria-label', '文件夹分页');

  const previousButton = document.createElement('button');
  previousButton.type = 'button';
  previousButton.className = 'page-arrow';
  previousButton.textContent = '‹';
  previousButton.title = '上一页';
  previousButton.disabled = state.folderPage <= 0;
  previousButton.addEventListener('click', () => setFolderPage(state.folderPage - 1));

  const nextButton = document.createElement('button');
  nextButton.type = 'button';
  nextButton.className = 'page-arrow';
  nextButton.textContent = '›';
  nextButton.title = '下一页';
  nextButton.disabled = state.folderPage >= pageCount - 1;
  nextButton.addEventListener('click', () => setFolderPage(state.folderPage + 1));

  previousButton.textContent = '‹';
  previousButton.title = '上一页';
  nextButton.textContent = '›';
  nextButton.title = '下一页';
  const firstButton = createPagerArrow('«', '首页', 0, state.folderPage <= 0, 'page-edge');
  const lastButton = createPagerArrow('»', '最后一页', pageCount - 1, state.folderPage >= pageCount - 1, 'page-edge');

  const pageButtons = document.createElement('div');
  pageButtons.className = 'page-buttons';

  const maxVisiblePages = window.matchMedia('(max-width: 520px)').matches ? 3 : 5;
  const startPage = Math.max(0, Math.min(state.folderPage - Math.floor(maxVisiblePages / 2), pageCount - maxVisiblePages));
  const endPage = Math.min(pageCount, startPage + maxVisiblePages);
  for (let pageIndex = startPage; pageIndex < endPage; pageIndex += 1) {
    pageButtons.append(createPageButton(pageIndex));
  }

  pager.append(firstButton, previousButton, pageButtons, nextButton, lastButton);
  return pager;
}

function measureFolderPageSize(gallery, folderList) {
  if (!gallery?.isConnected || !folderList.length) return 1;

  const availableHeight = Math.floor(gallery.clientHeight);
  const availableWidth = Math.floor(gallery.clientWidth);
  if (availableHeight <= 0 || availableWidth <= 0) {
    return state.folderPageSize;
  }

  const probe = document.createElement('section');
  probe.className = 'folder-gallery folder-measure';
  probe.style.width = `${availableWidth}px`;

  for (const folder of folderList) {
    probe.append(createFolderButton(folder));
  }

  document.body.append(probe);

  let capacity = 0;
  for (const child of Array.from(probe.children)) {
    if (child.offsetTop + child.offsetHeight <= availableHeight + 1) {
      capacity += 1;
    } else {
      break;
    }
  }

  probe.remove();
  return Math.max(1, capacity);
}

function scheduleFolderCapacityUpdate(folderList, gallery) {
  if (!folderList.length) return;

  requestAnimationFrame(() => {
    const measuredPageSize = measureFolderPageSize(gallery, folderList);
    if (measuredPageSize === state.folderPageSize) return;

    state.folderPageSize = measuredPageSize;
    const pageCount = Math.max(1, Math.ceil(folderList.length / state.folderPageSize));
    state.folderPage = Math.min(state.folderPage, pageCount - 1);
    renderFolderGallery(folderList);
  });
}

function renderFolderGallery(folders) {
  elements.mediaGrid.replaceChildren();

  const folderList = folders || [];
  const pageSize = Math.max(1, state.folderPageSize);
  const pageCount = Math.max(1, Math.ceil(folderList.length / pageSize));
  state.folderPage = Math.min(state.folderPage, pageCount - 1);
  const pageStart = state.folderPage * pageSize;
  const visibleFolders = folderList.slice(pageStart, pageStart + pageSize);

  const shell = document.createElement('section');
  shell.className = 'folder-page-shell';

  const actions = document.createElement('div');
  actions.className = 'folder-actions';
  actions.append(createRandomButton(), createUploadButton());

  const gallery = document.createElement('section');
  gallery.className = 'folder-gallery';
  gallery.setAttribute('aria-label', '文件夹');
  for (const folder of visibleFolders) {
    gallery.append(createFolderButton(folder));
  }

  shell.append(actions, gallery);

  if (!folderList.length) {
    const empty = document.createElement('p');
    empty.className = 'empty';
    empty.textContent = '这里暂时没有可显示的照片、视频或文件夹。';
    shell.append(empty);
  }

  const pager = renderFolderPager(pageCount);
  if (pager) shell.append(pager);
  elements.mediaGrid.append(shell);
  scheduleFolderCapacityUpdate(folderList, gallery);
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

function wrappedMediaIndex(index) {
  if (!state.mediaFiles.length) return 0;
  return ((index % state.mediaFiles.length) + state.mediaFiles.length) % state.mediaFiles.length;
}

function setActiveMediaIndex(index) {
  state.activeMediaIndex = wrappedMediaIndex(index);
  renderMediaDeck();
}

function navigateMedia(direction) {
  setActiveMediaIndex(state.activeMediaIndex + direction);
}

function navigateToPreviousMedia() {
  if (state.activeMediaIndex <= 0) {
    renderMediaDeck();
    return;
  }

  state.deckAnimation = 'previous';
  setActiveMediaIndex(state.activeMediaIndex - 1);
}

function createVideoPlaceholder(file) {
  const placeholder = document.createElement('div');
  placeholder.className = 'deck-media video-placeholder';
  placeholder.setAttribute('aria-label', file.name);
  placeholder.innerHTML = '<span>视频</span>';
  return placeholder;
}

function createImagePlaceholder(file) {
  const placeholder = document.createElement('div');
  placeholder.className = 'deck-media video-placeholder';
  placeholder.setAttribute('aria-label', file.name);
  placeholder.innerHTML = '<span>无法预览</span>';
  return placeholder;
}

function createImageElement(file, primaryUrl, fallbackUrl, isPriority, className = 'deck-media') {
  const image = document.createElement('img');
  image.className = className;
  image.loading = isPriority ? 'eager' : 'lazy';
  image.alt = file.name;
  image.src = primaryUrl;
  image.decoding = 'async';
  image.fetchPriority = isPriority ? 'high' : 'low';
  let didFallback = false;
  image.addEventListener('error', () => {
    if (!didFallback && primaryUrl !== fallbackUrl && file.usesConvertedPreview) {
      didFallback = true;
      image.src = fallbackUrl;
      return;
    }
    image.replaceWith(createImagePlaceholder(file));
  });
  return image;
}

function createMediaElement(file, isPriority) {
  if (file.type === 'image' || file.thumbUrl) {
    if (file.type === 'video') {
      const image = document.createElement('img');
      image.className = 'deck-media video-poster';
      image.loading = isPriority ? 'eager' : 'lazy';
      image.alt = file.name;
      image.src = apiUrl(file.thumbUrl || file.viewUrl).toString();
      image.decoding = 'async';
      image.fetchPriority = isPriority ? 'high' : 'low';
      image.addEventListener('error', () => {
        image.replaceWith(createVideoPlaceholder(file));
      }, { once: true });
      return image;
    }

    const thumbnailUrl = apiUrl(file.thumbUrl || file.viewUrl).toString();
    return createImageElement(file, thumbnailUrl, thumbnailUrl, isPriority);
  }

  return createVideoPlaceholder(file);
}

function mediaTypeLabel(file) {
  return file.type === 'image' ? '照片' : '视频';
}

function mediaFolderLabel(file) {
  const relativePath = String(file.path || '').replace(/\\/g, '/');
  const lastSlash = relativePath.lastIndexOf('/');
  return lastSlash > 0 ? relativePath.slice(0, lastSlash) : '影像备份';
}

function createDeckLabel(file, absoluteIndex) {
  const label = document.createElement('div');
  label.className = 'deck-label';

  const typeLine = document.createElement('span');
  typeLine.textContent = `${mediaTypeLabel(file)} · ${absoluteIndex + 1}/${state.mediaFiles.length}`;

  const title = document.createElement('strong');
  title.textContent = file.name;

  const detail = document.createElement('small');
  detail.textContent = state.isRandomMode ? `文件夹：${mediaFolderLabel(file)}` : formatBytes(file.size);

  label.append(typeLine, title, detail);
  return label;
}

function createDeckCard(file, absoluteIndex, options = {}) {
  const isTopCard = Boolean(options.isTopCard);
  const card = document.createElement('article');
  card.className = `deck-card${isTopCard ? ' active' : ''}${options.extraClass ? ` ${options.extraClass}` : ''}`;
  card.style.setProperty('--stack-index', String(options.stackIndex || 0));
  card.style.zIndex = String(options.zIndex || 10);

  const preview = document.createElement('div');
  preview.className = 'deck-preview';
  preview.append(createMediaElement(file, Boolean(options.isPriority)));

  card.append(preview, createDeckLabel(file, absoluteIndex));
  return card;
}

function setPreviousPeekPosition(card, deltaX, animate = false) {
  if (!card) return;
  const progress = Math.min(Math.max(deltaX / 180, 0), 1);
  const offset = Math.min(-window.innerWidth + deltaX * 1.18, 0);
  card.style.transition = animate ? 'transform 180ms ease, opacity 180ms ease' : 'none';
  card.style.opacity = String(Math.min(1, 0.18 + progress * 0.82));
  card.style.transform = `translate(${offset}px, ${24 - progress * 24}px) rotate(${-18 + progress * 18}deg)`;
}

function removePreviousPeek(card) {
  if (!card) return;
  card.style.transition = 'transform 160ms ease, opacity 160ms ease';
  card.style.opacity = '0';
  card.style.transform = `translate(${-window.innerWidth}px, 24px) rotate(-18deg)`;
  setTimeout(() => card.remove(), 170);
}

function ensurePreviousPeek(card) {
  if (!state.drag || state.drag.previousCard || state.mediaFiles.length <= 1 || !card.parentElement) {
    return state.drag?.previousCard || null;
  }

  const previousIndex = wrappedMediaIndex(state.activeMediaIndex - 1);
  const previousCard = createDeckCard(state.mediaFiles[previousIndex], previousIndex, {
    extraClass: 'previous-peek',
    isPriority: true,
    zIndex: 12
  });
  setPreviousPeekPosition(previousCard, 0);
  card.parentElement.append(previousCard);
  state.drag.previousCard = previousCard;
  state.drag.previousIndex = previousIndex;
  card.style.zIndex = '11';
  return previousCard;
}

function resetActiveCard(card) {
  card.style.transform = '';
  card.style.opacity = '';
  card.style.zIndex = '';
  card.style.removeProperty('--swipe-progress');
  delete card.dataset.swipeLabel;
}

function attachSwipeHandlers(card) {
  card.addEventListener('pointerdown', (event) => {
    if (elements.viewerDialog.open) return;
    if (event.button !== 0) return;
    state.drag = {
      startX: event.clientX,
      startY: event.clientY,
      moved: false,
      previousCard: null
    };
    card.classList.add('dragging');
    card.setPointerCapture(event.pointerId);
  });

  card.addEventListener('pointermove', (event) => {
    if (!state.drag) return;
    const deltaX = event.clientX - state.drag.startX;
    const deltaY = event.clientY - state.drag.startY;
    state.drag.moved = state.drag.moved || Math.abs(deltaX) > 6 || Math.abs(deltaY) > 6;
    if (deltaX < 0) {
      const rotation = deltaX / 18;
      card.style.transform = `translate(${deltaX}px, ${deltaY}px) rotate(${rotation}deg)`;
    } else {
      card.style.transform = '';
    }
    card.style.setProperty('--swipe-progress', String(Math.min(Math.abs(deltaX) / 160, 1)));
    if (deltaX > 0) {
      setPreviousPeekPosition(ensurePreviousPeek(card), deltaX);
    } else if (state.drag.previousCard) {
      removePreviousPeek(state.drag.previousCard);
      state.drag.previousCard = null;
      state.drag.previousIndex = undefined;
      card.style.zIndex = '';
    }
    card.dataset.swipeLabel = deltaX < 0 ? '下一张' : '上一张';
  });

  card.addEventListener('pointerup', (event) => {
    if (!state.drag) return;
    const deltaX = event.clientX - state.drag.startX;
    const deltaY = event.clientY - state.drag.startY;
    const moved = state.drag.moved;
    const tapped = Math.abs(deltaX) <= TAP_SLOP && Math.abs(deltaY) <= TAP_SLOP;
    const previousCard = state.drag.previousCard;
    const previousIndex = state.drag.previousIndex;
    state.drag = null;
    card.classList.remove('dragging');
    try {
      card.releasePointerCapture(event.pointerId);
    } catch {}

    if (deltaX < 0) {
      removePreviousPeek(previousCard);
      card.style.transform = `translate(${-window.innerWidth}px, 24px) rotate(-18deg)`;
      setTimeout(() => navigateMedia(1), 180);
      return;
    }

    if (deltaX > 0) {
      const activePreviousIndex = previousIndex ?? wrappedMediaIndex(state.activeMediaIndex - 1);
      let activePreviousCard = previousCard;
      if (!activePreviousCard && state.mediaFiles.length > 1 && card.parentElement) {
        activePreviousCard = createDeckCard(state.mediaFiles[activePreviousIndex], activePreviousIndex, {
          extraClass: 'previous-peek',
          isPriority: true,
          zIndex: 12
        });
        setPreviousPeekPosition(activePreviousCard, deltaX);
        card.parentElement.append(activePreviousCard);
      }
      if (activePreviousIndex === undefined) {
        removePreviousPeek(activePreviousCard);
        resetActiveCard(card);
        return;
      }

      resetActiveCard(card);
      setPreviousPeekPosition(activePreviousCard, window.innerWidth, true);
      setTimeout(() => {
        if (activePreviousCard) activePreviousCard.remove();
        state.activeMediaIndex = activePreviousIndex;
        renderMediaDeck();
      }, 180);
      return;
    }

    removePreviousPeek(previousCard);
    resetActiveCard(card);
    if (!moved && tapped) {
      openViewer(currentMediaFile());
    }
  });

  card.addEventListener('pointercancel', (event) => {
    const previousCard = state.drag?.previousCard;
    state.drag = null;
    card.classList.remove('dragging');
    try {
      card.releasePointerCapture(event.pointerId);
    } catch {}
    removePreviousPeek(previousCard);
    resetActiveCard(card);
  });
}

function resetDeckInteraction() {
  const activeDraggingCards = elements.mediaGrid.querySelectorAll('.deck-card.dragging');
  for (const card of activeDraggingCards) {
    card.classList.remove('dragging');
    resetActiveCard(card);
  }

  const peekCards = elements.mediaGrid.querySelectorAll('.deck-card.previous-peek');
  for (const card of peekCards) {
    card.remove();
  }

  state.drag = null;
}

function preloadUpcomingThumbnails() {
  const preloadFiles = [];
  const seenIndexes = new Set();

  for (let offset = -5; offset <= 5; offset += 1) {
    const index = wrappedMediaIndex(state.activeMediaIndex + offset);
    if (seenIndexes.has(index)) continue;
    seenIndexes.add(index);
    const file = state.mediaFiles[index];
    preloadFiles.push(file);
  }

  for (const file of preloadFiles) {
    if (!file.thumbUrl) continue;
    const image = new Image();
    image.decoding = 'async';
    image.fetchPriority = 'low';
    image.src = apiUrl(file.thumbUrl).toString();
  }
}

function scheduleThumbnailPreload() {
  if (state.preloadTimer) {
    clearTimeout(state.preloadTimer);
  }

  state.preloadTimer = setTimeout(() => {
    state.preloadTimer = null;
    preloadUpcomingThumbnails();
  }, 80);
}

function renderMediaDeck() {
  elements.mediaGrid.replaceChildren();
  const incomingFromPrevious = state.deckAnimation === 'previous';
  state.deckAnimation = null;

  const deckShell = document.createElement('section');
  deckShell.className = 'deck-shell';

  const deck = document.createElement('div');
  deck.className = 'deck';

  const visibleFiles = state.mediaFiles.slice(state.activeMediaIndex, state.activeMediaIndex + 3);
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

    if (isTopCard && incomingFromPrevious) {
      card.classList.add('returning');
      card.style.transform = `translate(${window.innerWidth}px, 24px) rotate(18deg)`;
      requestAnimationFrame(() => {
        requestAnimationFrame(() => {
          card.classList.remove('returning');
          card.style.transform = '';
        });
      });
    }

    const preview = document.createElement('div');
    preview.className = 'deck-preview';
    preview.append(createMediaElement(file, isTopCard));

    card.append(preview, createDeckLabel(file, absoluteIndex));
    deck.append(card);
  });

  const controls = document.createElement('div');
  controls.className = 'deck-controls';

  const downloadLink = document.createElement('a');
  downloadLink.className = 'button-link';
  downloadLink.textContent = '下载';
  downloadLink.href = apiUrl(currentMediaFile().downloadUrl).toString();
  downloadLink.setAttribute('download', currentMediaFile().name);
  downloadLink.addEventListener('click', markDownloadStarting);

  const jumpForm = document.createElement('form');
  jumpForm.className = 'deck-jump';
  jumpForm.setAttribute('aria-label', '跳转到指定序号');
  const sequenceTotal = state.mediaFiles.length;

  const jumpInput = document.createElement('input');
  jumpInput.type = 'number';
  jumpInput.inputMode = 'numeric';
  jumpInput.min = '1';
  jumpInput.max = String(sequenceTotal);
  jumpInput.placeholder = '序号';
  jumpInput.value = String(state.activeMediaIndex + 1);
  jumpInput.setAttribute('aria-label', `输入 1 到 ${sequenceTotal} 之间的照片序号`);

  const jumpButton = document.createElement('button');
  jumpButton.type = 'submit';
  jumpButton.textContent = '查看';

  jumpForm.addEventListener('submit', (event) => {
    event.preventDefault();
    const targetIndex = Number(jumpInput.value);
    const isValidIndex = Number.isInteger(targetIndex) && targetIndex >= 1 && targetIndex <= sequenceTotal;
    jumpInput.classList.toggle('is-invalid', !isValidIndex);
    jumpInput.setAttribute('aria-invalid', String(!isValidIndex));
    if (!isValidIndex) {
      jumpInput.setCustomValidity(`应输入 1-${sequenceTotal}`);
      jumpInput.reportValidity();
      return;
    }
    setActiveMediaIndex(targetIndex - 1);
  });

  jumpInput.addEventListener('input', () => {
    jumpInput.classList.remove('is-invalid');
    jumpInput.removeAttribute('aria-invalid');
    jumpInput.setCustomValidity('');
  });

  jumpForm.append(jumpInput, jumpButton);
  controls.append(downloadLink, jumpForm);
  deckShell.append(deck, controls);
  elements.mediaGrid.append(deckShell);
  scheduleThumbnailPreload();
}

async function openFolder(folderPath = '') {
  state.currentFolder = folderPath;
  state.isRandomMode = false;
  elements.currentFolder.textContent = folderTitle(folderPath);
  setConnectionStatus(true, '后端已连接，正在读取影像备份。');
  elements.mediaCount.textContent = '正在读取文件夹...';
  elements.rootButton.hidden = true;

  try {
    const data = await requestJson('/api/media', { folder: folderPath });
    setStatus(data.status);
    state.folders = data.folders || [];
    state.folderPage = 0;
    renderMedia(data.files || [], state.folders);
    elements.rootButton.hidden = !state.mediaFiles.length;

    const mediaText = `${data.files?.length || 0} 个照片/视频`;
    elements.mediaCount.textContent = state.mediaFiles.length
      ? mediaText
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
    elements.rootButton.hidden = true;
  }
}

async function openRandomMode() {
  state.currentFolder = '';
  state.isRandomMode = true;
  elements.currentFolder.textContent = '随机探索';
  setConnectionStatus(true, '后端已连接，正在随机扫描所有子文件夹的照片。');
  elements.mediaCount.textContent = '正在随机抽取照片...';
  elements.rootButton.hidden = true;

  try {
    const data = await requestJson('/api/random', { limit: 96 });
    setStatus(data.status);
    state.folders = [];
    renderMedia(data.files || [], []);
    elements.rootButton.hidden = !state.mediaFiles.length;
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
    elements.rootButton.hidden = true;
  }
}

function openViewer(file) {
  if (!file) return;

  resetDeckInteraction();
  elements.viewerTitle.textContent = file.name;
  elements.viewerStage.replaceChildren();

  const source = apiUrl(file.previewUrl || file.viewUrl).toString();
  const downloadUrl = apiUrl(file.downloadUrl).toString();
  elements.downloadLink.href = downloadUrl;
  elements.downloadLink.setAttribute('download', file.name);

  if (file.type === 'image') {
    const fallbackUrl = source;
    const image = createImageElement(file, fallbackUrl, fallbackUrl, true, '');
    elements.viewerStage.append(image);
  } else {
    const video = document.createElement('video');
    video.className = 'viewer-video';
    video.controls = true;
    video.preload = 'none';
    video.playsInline = true;
    video.disablePictureInPicture = false;
    video.src = source;
    elements.viewerStage.append(video);
  }

  elements.viewerDialog.showModal();
}

function clearViewerStage() {
  const videos = elements.viewerStage.querySelectorAll('video');
  for (const video of videos) {
    video.pause();
    video.removeAttribute('src');
    video.load();
  }
  elements.viewerStage.replaceChildren();
  resetDeckInteraction();
  if (state.mediaFiles.length) {
    renderMediaDeck();
  }
}

function folderOptionLabel(folder) {
  return folder.path;
}

function populateUploadFolders(selectedPath = '') {
  elements.uploadFolderSelect.replaceChildren();
  elements.uploadFolderSelect.disabled = Boolean(elements.newFolderInput.value.trim());
  const folders = state.uploadFolders.filter((folder) => folder.path);

  for (const folder of folders) {
    const option = document.createElement('option');
    option.value = folder.path;
    option.textContent = folderOptionLabel(folder);
    option.selected = folder.path === selectedPath;
    elements.uploadFolderSelect.append(option);
  }

  if (selectedPath && folders.some((folder) => folder.path === selectedPath) && elements.uploadFolderSelect.value !== selectedPath) {
    const option = document.createElement('option');
    option.value = selectedPath;
    option.textContent = selectedPath;
    option.selected = true;
    elements.uploadFolderSelect.prepend(option);
  }

  if (!folders.length) {
    const option = document.createElement('option');
    option.value = '';
    option.textContent = '可新建文件夹后上传';
    option.disabled = true;
    option.selected = true;
    elements.uploadFolderSelect.append(option);
  }
}

function showUploadFolderLoading() {
  elements.uploadFolderSelect.replaceChildren();
  elements.uploadFolderSelect.disabled = true;
  const option = document.createElement('option');
  option.value = '';
  option.textContent = '文件夹加载中';
  option.selected = true;
  elements.uploadFolderSelect.append(option);
}

function syncUploadTargetState() {
  const creatingFolder = Boolean(elements.newFolderInput.value.trim());
  elements.uploadFolderSelect.disabled = creatingFolder;
  if (creatingFolder) {
    elements.uploadStatus.textContent = '新建文件夹会创建在影像备份下，与其它子文件夹平级。';
  } else if (elements.uploadStatus.textContent === '新建文件夹会创建在影像备份下，与其它子文件夹平级。') {
    elements.uploadStatus.textContent = '';
  }
}

async function loadUploadFolders(selectedPath = '') {
  elements.uploadStatus.textContent = '正在读取可上传的文件夹...';
  showUploadFolderLoading();

  try {
    const data = await requestJson('/api/upload-folders');
    state.uploadFolders = data.folders || [];
    setStatus(data.status);
    populateUploadFolders(selectedPath);
    syncUploadTargetState();
    elements.uploadStatus.textContent = '';
  } catch (error) {
    populateUploadFolders(selectedPath);
    syncUploadTargetState();
    elements.uploadStatus.textContent = '文件夹列表读取失败，可填写新建文件夹后上传。';
  }
}

async function openUploadDialog() {
  const selectedPath = state.isRandomMode ? '' : state.currentFolder;
  elements.newFolderInput.value = '';
  elements.uploadPasswordInput.value = '';
  elements.uploadFilesInput.value = '';
  elements.uploadStatus.textContent = '';
  resetUploadProgress();
  populateUploadFolders(selectedPath);
  elements.uploadDialog.showModal();
  await loadUploadFolders(selectedPath);
}

elements.rootButton.addEventListener('click', () => openFolder(''));
elements.closeUploadButton.addEventListener('click', () => {
  elements.uploadDialog.close();
});
elements.uploadForm.addEventListener('submit', (event) => {
  event.preventDefault();
  uploadFiles();
});
elements.newFolderInput.addEventListener('input', syncUploadTargetState);
elements.downloadLink.addEventListener('click', markDownloadStarting);
elements.closeViewerButton.addEventListener('click', () => {
  elements.viewerDialog.close();
});

elements.viewerDialog.addEventListener('close', () => {
  clearViewerStage();
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

window.addEventListener('resize', () => {
  if (state.resizeTimer) {
    clearTimeout(state.resizeTimer);
  }

  state.resizeTimer = setTimeout(() => {
    state.resizeTimer = null;
    if (!state.mediaFiles.length) {
      state.folderPageSize = DEFAULT_FOLDER_PAGE_SIZE;
      renderFolderGallery(state.folders);
    }
  }, 120);
});

startBackendHeartbeat();
openFolder('');
