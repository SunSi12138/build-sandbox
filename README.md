# CommunityGIS 社区管理

本地社区 GIS 办公工具，源于 hbtool。源码为 .NET 10 Native AOT + SQLite、Nuxt + Nuxt UI，地图采用本地 OpenLayers，使用原 Map 的吉林一号缓存。

**直接使用：** 打开桌面本项目的 `运行版`，双击 `启动社区管理.cmd`。运行环境无需安装 .NET、Node.js 或 Office。

- [开始使用](docs/开始使用.txt)：场所、多用途、检查整改、手机访问、导出和备份。
- [开发与交付](docs/开发与交付.txt)：构建、数据版本及验证范围。
- [开发记录](docs/COMMUNITY-PLAN.md)：实现与检查记录。

电脑负责运行，手机连接同一局域网使用同一套页面。地图引擎、图标、图片压缩组件全部本地化；断开互联网仍可使用，地图仅覆盖已缓存区域。

地图支持父子网格、连续绘制、多用途标签和独立颜色。用途字段可配置文本、数字、必填与顺序。图层统一自动缓存，“更新缓存”控制是否重新获取已有瓦片。

地图服务配置放在 `map-providers.local.json`，不提交到源码库；本机可用 `scripts/import-map-provider-config.py` 从原 Map 导入，构建脚本会将配置放入运行版。

正式数据首次启动时创建，演示/测试数据留在 work 目录。不要把测试目录当正式运行版。

构建：PowerShell 执行 `./scripts/build-community.ps1`。源码在 server 和 web 目录。

旧 hbtool 说明保存在 docs/HBTOOL-REFERENCE.md，仅供来源参考。
