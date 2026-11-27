# GMCM 配置文本本地化（GMCM Config Text Localizer）

为 GMCM 配置界面中的选项名称、说明、分组文字、段落、分页标题和下拉选项提供本地译文。

## 同步翻译

安装 SMAPI 4.1 或更高版本和 GMCM 1.13 或更高版本。将构建产物 `GMCMConfigTextLocalizer.dll`、`manifest.json` 和 `i18n` 一起放入游戏的 `Mods/GMCMConfigTextLocalizer` 目录。

每次启动游戏时，模组会从 GitHub 的 `community-packs` 分支获取译文，并将缺少的条目补入本地 `overrides/<语言代码>/<模组ID>.json`。同一个文件里已有的原文键会保留原值，包括留空的条目。已同步的译文可以离线使用。

也可以在 GMCM 中打开「GMCM 配置文本本地化」并点击「同步社区翻译」，或在 SMAPI 控制台运行 `gmcmtext_sync`。同步后重新打开对应设置页即可查看当前语言的译文。

GMCM 根据原文精确匹配本地 `overrides` 中的译文。删除已同步的键后，下次同步会补回该条目。要保留原文，可以在 JSON 中把该键的译文设为空字符串。

## 在配置页编辑译文

打开目标模组的 GMCM 配置页，点击右上角的「编辑译文」。在左侧选择当前页的文字，在右侧查看原文、输入译文并实时预览。点击「应用」后返回原页面并立即显示译文，原页面未保存的配置值会保留。

译文保存到当前游戏语言的 `overrides/<语言代码>/<模组ID>.json`。简体中文和导出共用 `overrides/zh/`，已有该模组的 `zh-CN` 覆盖文件时会继续编辑该文件。清空译文并应用会移除对应的本地覆盖，显示下一优先级的译文或原文。点击「取消」会丢弃面板中的未应用修改。同一模组里完全相同的原文共用一条译文。

面板右上角的「导出」会直接打开当前模组的条目选择页。关闭导出菜单后可以继续编辑草稿。「外部编辑」使用系统关联的应用打开同一份覆盖文件，首次打开会创建空文件。保存外部修改后，返回 GMCM 配置页即可看到新译文。

## 本地翻译与投稿

在本模组的 GMCM 设置页点击「导出翻译」，在左侧勾选要导出的模组，在右侧选择条目。默认勾选全部模组，并选中所有待翻译条目。取消「仅显示待翻译」可以查看和勾选其他条目。鼠标悬停可查看完整原文。模板按当前游戏语言保存到 `overrides/<语言代码>/`，简体中文使用 `overrides/zh/`。已有译文会保留。

运行 `gmcmtext_export zh` 导出缺失的中文翻译，也可附加模组 ID 只检查指定模组。模板写入模组目录的 `overrides/zh/`。左边是原文，在右边的双引号里填写译文。一个模组里相同的原文只需翻译一次。

```json
{
  "Enable mod": "启用模组",
  "Fish quality": ""
}
```

重新导出会保留已填写的译文。保存后重新打开 GMCM 页面即可查看译文。

导出时会重新读取 GMCM 的文字回调。比对目标模组的语言文件时会识别常见的缩进、箭头和已填入的参数。

从本机发布译文时，在仓库根目录运行 `python publish-translations.py`。脚本会从游戏 Mods 目录的 `overrides` 读取新增和修订的译文，校验后只提交仓库 `overrides/` 中涉及的文件，并推送到 `origin/main`。脚本会跳过空译文和标记为「无需翻译」的条目。添加 `--dry-run` 可以预览。游戏装在其他位置时，把游戏内的 `overrides` 完整路径作为参数传入。

要分享翻译，fork 本仓库，将填写好的 JSON 放在 `overrides/<语言代码>/<模组UniqueID>.json`，例如 `overrides/zh/Bouhm.NPCMapLocations.json`。将变更推到自己的 fork，再向本仓库的 `main` 分支发起 Pull Request。若已有同语言、同模组文件，请将新译文合并到该文件。合并后，GitHub Actions 会发布翻译包，其他玩家可在发布完成后同步。[Fork 本仓库](https://github.com/iMoonDay/GMCMConfigTextLocalizer/fork)

To contribute a translation, fork this repository, add or update `overrides/<locale>/<mod UniqueID>.json` with a flat original-to-translation JSON object, and open a pull request against `main`. GitHub Actions publishes the translations to `community-packs` for in-game sync.

## 构建

```powershell
dotnet build GMCMConfigTextLocalizer.csproj -c Release -p:GamePath="E:/SteamLibrary/steamapps/common/Stardew Valley"
```
