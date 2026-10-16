# 第三方组件

眠川署名适用于本项目维护部分；第三方组件保留各自作者和许可证。

| 组件 | 用途 | 来源 |
| --- | --- | --- |
| AssetsTools.NET / class.tpk | Unity 资源包读写与类型信息 | https://github.com/nesrak1/AssetsTools.NET |
| LZMA SDK | 原生 LZMA 压缩 | https://www.7-zip.org/sdk.html；声明见 native/LZMA-SDK-NOTICE.txt |
| XZ / liblzma | 世界包 LZMA 处理 | https://tukaani.org/xz/；声明见 tools/xz-COPYING.0BSD.txt |
| librsync.net | 文件分块校验数据生成 | [braddodson/librsync.net](https://github.com/braddodson/librsync.net)，上游 MIT；原文见 [licenses/librsync.net-MIT.txt](licenses/librsync.net-MIT.txt)，当前旧 DLL 的源码对应关系仍待核验 |
| Blake2Sharp | BLAKE2 分块摘要，供 librsync.net 使用 | [BLAKE2/BLAKE2](https://github.com/BLAKE2/BLAKE2)，CC0-1.0；原文见 [licenses/Blake2Sharp-CC0.txt](licenses/Blake2Sharp-CC0.txt)；当前 DLL 与 librsync.net 上游随附文件完全一致 |
| netstandard.dll | .NET 标准接口兼容 | Microsoft .NET |

本项目维护的代码采用 AGPL-3.0-only，见根目录 LICENSE。该声明不改变上述第三方组件的原有许可证。

## librsync.net / Blake2Sharp 来源核对

核对日期：2026-10-16。本次只补充来源与许可文本，未替换任何运行依赖。

- `librsync.net` 的许可与 README 固定参考提交为 [`abcda421a74d769b2be986c0b92a41c12a4f96ce`](https://github.com/braddodson/librsync.net/tree/abcda421a74d769b2be986c0b92a41c12a4f96ce)。MIT 原文版权人为 Brad Dodson，2015；再分发时需保留其版权与许可声明。
- 上游 README 说明这是 rsync 算法的独立 C# 实现，并说明随附的 Blake2 DLL 基于 BLAKE2 的 CC0 代码。不要将原生 `librsync` 项目的许可证直接当作本 .NET 实现的许可证。
- `Blake2Sharp` 的 CC0 原文固定参考 BLAKE2 提交 [`ed1974ea83433eba7b2d95c5dcd9ac33cb847913`](https://github.com/BLAKE2/BLAKE2/tree/ed1974ea83433eba7b2d95c5dcd9ac33cb847913)。其 C# 程序集信息标注 `CodesInChaos`、`Public Domain`，与本地 DLL 元数据一致。

| 当前文件 | SHA-256 | 核验状态 |
| --- | --- | --- |
| `librsync.net.dll` | `8720FEBA09DDCB976928EC464BB4533DBEF421918099A456566A5CA80C3F7968` | 版本与版权字段吻合候选上游，但上游未附此 DLL；尚未完成二进制与源码对应核验。MIT 为已确认的上游许可，不代表已证明当前 DLL 的全部来源 |
| `Blake2Sharp.dll` | `AD9CF70D07E87860D2635EF855E0613EDFDE3A9BB67795E52B630523BF5F6D9C` | Git blob `43b09138b34890a27572bb4f0133d2cb9c3f6a94` 与上述 librsync.net 固定提交随附 DLL 完全一致；未完成独立源码可重现构建 |

这里的 `signature` 是上传使用的文件分块校验数据，不是世界的 `worldSignature` 或私钥数字签名。公开发行前仍应完成旧 `librsync.net.dll` 的来源核验，以及其他第三方二进制、类型库的版本与分发声明检查。本文件不是全部依赖已完成许可审计的证明。
