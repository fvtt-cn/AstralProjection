# AstralProjection
星界投影，用于镜像 FVTT 的系统和 MOD，上传到对象存储并使用 CDN 加速。

## 使用方法
使用方法是在安装前，替换清单文件（Manifest URL）的链接：

比如当你需要安装 dnd5e 游戏系统时，从官网或中文社区 MOD 介绍中复制到的，它的清单文件地址是：`https://gitlab.com/foundrynet/dnd5e/raw/master/system.json`

在这个地址的 `https://` 的后面，加上一段 `cdn.sbea.in/`，即替换为国内镜像地址。

上述例子中，用此法修改，链接就会变为：`https://cdn.sbea.in/gitlab.com/foundrynet/dnd5e/raw/master/system.json`

然后在 FVTT 中填写清单文件地址处，粘贴这个链接即可使用快速的国内镜像进行安装。
