# 第三方组件

发布包中的 `mpv.exe` 来自 [mpv 官方安装页列出的 Windows 构建](https://mpv.io/installation/)，具体构建为 [shinchiro 2026-09-28 x86_64](https://github.com/shinchiro/mpv-winbuild-cmake/releases/tag/20260928)。下载压缩包的 SHA-256 为 `6491ba670f836553fdd0d965c6e99ed8aa4053c1c79a5e4e30a1e127da64714a`。mpv 及其依赖的授权条款、源码获取方式以其[上游仓库](https://github.com/mpv-player/mpv)和[构建仓库](https://github.com/shinchiro/mpv-winbuild-cmake)为准。

FFmpeg/ffprobe 不打包进应用；程序使用电脑已安装的版本，缺少时运行 `winget install -e --id Gyan.FFmpeg`。
