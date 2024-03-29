# AstralProjection
星界投影，用于镜像 FVTT 的系统和 MOD，上传到对象存储并使用 CDN 加速。

## 如何使用国内镜像解决系统和 MOD 难以安装的问题？

众所周知，由于墙和出口带宽的原因，国内服务器或者国内无梯用户安装游戏系统和 MOD 时候的下载速度比较缓慢或者没有速度，表现出来的结果就是常常系统/MOD 安装失败。

为此 FVTT-CN 社区开发了 [Astral Projection（星界投影）](https://github.com/fvtt-cn/AstralProjection)，用于将 *部分* 游戏系统和 MOD 同步镜像到国内的服务器和 CDN 上，并且提供下载服务，方便国内用户安装和更方便地使用 FVTT。

**使用方法是在安装前，替换清单文件（Manifest URL）的链接：**

比如当你需要安装 `dnd5e` 游戏系统时，从官网或中文社区 MOD 介绍中复制到的，它的清单文件地址是：`https://gitlab.com/foundrynet/dnd5e/raw/master/system.json`。

**在这个地址的前面，加上一段 `https://cdn.sbea.in/`，即替换为国内镜像地址**。

上述例子中，用此法修改，链接就会变为：`https://cdn.sbea.in/https://gitlab.com/foundrynet/dnd5e/raw/master/system.json`。

然后在 FVTT 中填写清单文件地址处，粘贴这个链接即可使用快速的国内镜像进行安装。

### FVTT 软件镜像
为了提升境内服务器部署 FVTT 速度，星界投影镜像同时维护了 Linux 版本的 Foundry VTT 软件镜像。尤其是在境内使用脚本部署时，推荐使用。

**使用方法是在需要填写 FVTT 的下载地址时，填写为 `https://cdn.sbea.in/foundry/linux/foundryvtt-0.7.9.zip`**

以上链接中的 `0.7.9` 替换为你想要下载的版本号。注意，星界投影镜像源是主动获取 FVTT 官方软件包，有一定周期，建议在每一次最新版本发布的第二天以后再使用最新版本号进行部署。

### 镜像源在线状态

**星界投影**镜像有时会因为下线维护、或者使用量超过了为了节省成本设置的限额，导致服务器离线，无法使用。

判断星界投影源能否正常使用可以参见以下地址，如果显示`在线`则可以使用：<a href="https://cdn.sbea.in/status.html" target="_blank">星界投影在线状态</a>。

除此之外，在星界投影的[列表页面](https://cdn.sbea.in/list.html)，可以通过 <kbd>Ctrl</kbd>+<kbd>F</kbd> 快速检索星界投影已经镜像的清单文件地址。

### 请求同步新的 系统/MOD
在 [GitHub 项目](https://github.com/fvtt-cn/AstralProjection/issues/new/choose)上提出新 issue，填写好 MOD 名称、项目地址、清单文件地址，即提出同步请求。

### 支持国内镜像运营
运营、维护国内镜像需要一定成本，如果愿意的话，可以在[爱发电](https://afdian.net/@mitch)上支持 FVTT-CN 的开发和维护。
