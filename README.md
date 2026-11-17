# 1Remote Linux (`1remote-linux`)

基于 **.NET 10 + Avalonia UI** 构建的原生 Linux 远程桌面与终端管理客户端，专为 Ubuntu 原生图形桌面（Wayland / X11）打造，与 Windows 端 [Shermanxwz/1Remote](https://github.com/Shermanxwz/1Remote)（`v1.3.2+`）及上游 [1Remote/1Remote](https://github.com/1Remote/1Remote) **零转换共用同一个 MySQL / PostgreSQL / SQLite 数据库**。

## 核心亮点

1. **100% 同源数据库与加解密互通**
   - 直接复用官方 `1Remote.Security` 加密组件与相同的 `Servers`、`Configs`、`Credentials` 表结构及 `UpdateTimestamp_*` 增量同步协议。
   - 支持一键导入 Windows 端 `1Remote` 的 `AdditionalDataSource.json` 配置文件。
   - 通过 `[JsonExtensionData]` 无损保留 Windows 专有配置字段（如 `RdpControlAdditionalSettings`、图标等），在 Linux 端增删改设备不会冲掉 Windows 端的高级参数。
   - 内置连接池半开死连接自动 `ClearPool` 自愈重连与后台非重入增量同步锁，彻底杜绝 `database lost connection!`。

2. **内置原生 SSH 终端 + SFTP / FTP 双模文件管理**
   - 基于 `SSH.NET` 与 `FluentFTP` 原生实现，支持密码、OpenSSH 私钥（`id_ed25519` / `id_rsa`）及 PAM Keyboard-Interactive 验证。
   - 状态机级 UTF-8 多字节流解码，彻底解决跨 TCP 分片中文输出乱码问题；支持一键联动打开当前 SSH 主机的 SFTP 文件浏览器进行上传、下载、新建目录与删除。

3. **Ubuntu 桌面 FreeRDP (`xfreerdp3` / `xfreerdp`) 原生集成**
   - 默认按 `xfreerdp3` -> `xfreerdp` -> `wlfreerdp3` -> `sdl-freerdp3` 优先级自动探测客户端，自适应 Ubuntu Wayland 与 X11 桌面环境。
   - 默认开启 `/dynamic-resolution` 窗口拖拽自适应分辨率与 `+clipboard` 双向剪贴板同步，并通过 `/from-stdin` 标准输入安全注入密码，绝不在系统进程参数中泄露明文密码。

4. **修复版多会话标签栏与 `＋` 快速切设备抽屉**
   - 右上角内置非抢焦点式 `＋ 快速连新设备` 浮层抽屉，彻底避免在活跃远程会话中点 `+` 弹窗闪退消失的问题，且无任何好评/赞赏打扰弹窗。

## 快速安装（Ubuntu 22.04 / 24.04+）

前往 [Releases](https://github.com/Shermanxwz/1remote-linux/releases) 下载最新安装包：

### 方式一：Ubuntu / Debian `.deb` 安装包（推荐）
```bash
sudo apt update
sudo apt install -y ./1remote-linux_0.1.0_amd64.deb freerdp3-x11
# Ubuntu 22.04 若无 freerdp3-x11 可安装 freerdp2-x11：
# sudo apt install -y ./1remote-linux_0.1.0_amd64.deb freerdp2-x11

1remote-linux
```

### 方式二：自包含免安装压缩包（无需预装 .NET 10 Runtime）
```bash
mkdir -p ~/1remote-linux
tar -xzf 1remote-linux-0.1.0-linux-x64-self-contained.tar.gz -C ~/1remote-linux
~/1remote-linux/1remote-linux
```

## 快捷键
- `Ctrl + K`：聚焦左侧设备搜索框（支持名称、IP、端口、用户名、标签多词过滤与 IPv4 自然排序）
- `Ctrl + R`：立即从 MySQL / PostgreSQL 增量同步最新设备列表

## 开源协议
本项目遵循 [GPL-3.0 License](LICENSE)。
