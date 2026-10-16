# VRC 热上传

作者：眠川

许可证：GNU AGPL-3.0（`AGPL-3.0-only`），完整条款见 [LICENSE](LICENSE)。

Windows 桌面工具，支持 VRCA 模型上传、VRCW 世界上传、PC / Quest 世界配对、文件夹扫描和模型批量下载。本地处理资源包，使用 VRChat 账号登录。

## 使用

运行 `MianchuanUploader.exe`，登录 VRChat 后选择文件。运行环境为 Windows x64 与 .NET Framework 4.8。无需卡密或自建授权服务器。

## 构建

安装 MinGW-w64，将 `gcc.exe`、`g++.exe` 加入 PATH，然后在本目录运行：

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

已有原生 DLL 时，可用 `-ManagedOnly` 只编译 C# 部分。

## 文件

`src/` 为界面和业务源码，`native/` 为 CAB 与 LZMA 原生源码，`tools/` 为处理程序与运行依赖。日志和临时数据在首次运行后保存到 `_runtime/`。账号会话独立存储在当前用户的 `%LOCALAPPDATA%\Mianchuan.VrcHotUpload`。

## 许可

本项目维护的代码采用 GNU Affero General Public License 第 3 版。修改和分发时应遵守该许可证；通过网络向用户提供修改后的程序服务时，应按其第 13 条提供对应源码的获取方式。

第三方依赖保留各自的许可证和版权声明，见 [THIRD-PARTY.md](THIRD-PARTY.md)。
