# 影像备份图库

这个项目由两部分组成：

- `web/`：可以部署到 GitHub Pages 的网页前端。
- `server.js`：运行在你自己电脑上的本地后端，用来检测 `F:` 移动硬盘并读取 `F:\影像备份`。

浏览器里的 GitHub Pages 页面不能直接访问本机硬盘，这是浏览器的安全限制。因此使用流程是：电脑开机并插入移动硬盘后，在电脑上运行后端，网页前端通过后端地址调用它。

## 本机运行

```powershell
npm run start
```

打开：

```text
http://127.0.0.1:8787
```

默认媒体目录是：

```text
F:\影像备份
```

如果你的路径变化了，可以这样启动：

```powershell
$env:GALLERY_ROOT="F:\影像备份"; npm run start
```

## GitHub Pages

仓库包含 `.github/workflows/pages.yml`。推送到 `main` 分支后，GitHub Actions 会把 `web/` 发布为静态前端。

部署后的网页仍然需要你在本机运行后端服务；如果电脑关机、后端未启动或移动硬盘未插入，前端会显示连接/硬盘状态错误。

## 让别人直接访问

GitHub Pages 不能把你的移动硬盘变成公网服务。要让别人直接访问，需要给本机后端一个公网 HTTPS 地址，常见做法是使用 Cloudflare Tunnel、ngrok、frp 或自有服务器反向代理。

推荐至少设置访问令牌：

```powershell
$env:GALLERY_TOKEN="换成一个足够长的随机密码"; npm run start
```

拿到公网后端地址后，有两种接入方式：

1. 临时分享链接：

```text
https://zxn091651.github.io/my_photo_gallery/?api=https%3A%2F%2F你的公网后端地址&token=你的访问令牌
```

2. 固定写入 `web/config.js`：

```js
window.GALLERY_CONFIG = {
  apiBase: "https://你的公网后端地址",
  token: ""
};
```

如果把 token 写进 `web/config.js`，任何能打开网页源码的人都能看到它；更建议只把 token 通过私下链接发给可信的人。

## API

- `GET /api/status`：检测电脑后端、F 盘、卷标和媒体根目录状态。
- `GET /api/folders`：递归列出 `影像备份` 下的所有文件夹。
- `GET /api/media?folder=相对路径`：列出某个文件夹下的子文件夹、照片和视频。
- `GET /api/file?path=相对路径`：预览照片/视频，支持视频 Range 请求。
- `GET /api/download?path=相对路径`：下载文件。

支持的照片格式包括 `jpg`、`png`、`webp`、`gif`、`bmp`、`avif`、`tif`、`heic` 等；视频格式包括 `mp4`、`webm`、`mov`、`m4v`、`avi`、`mkv`、`mts`、`m2ts`、`3gp` 等。
