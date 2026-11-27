# GMCM 配置文本本地化（GMCM Config Text Localizer）

为 GMCM 配置界面中的选项名称、说明、分组文字、段落、分页标题和下拉选项查找翻译。模组会优先使用目标模组自己的 i18n 翻译，并在找不到时使用本模组的覆盖文件。

## 同步翻译

安装 SMAPI 4.1 或更高版本和 GMCM 1.13 或更高版本。将构建产物 `GMCMConfigTextLocalizer.dll`、`manifest.json` 和 `i18n` 一起放入游戏的 `Mods/GMCMConfigTextLocalizer` 目录。

每次启动游戏时，模组会在后台读取 GitHub 上的 `version.txt`，并与本地翻译包 ZIP 内的同名文件比较。首次使用或版本有变化时才下载 ZIP，校验包内版本后更新缓存。解压翻译包即可查看版本。该版本由 `overrides` 目录的 Git 树哈希自动生成，随翻译文件变化。检查期间和离线时都可以使用最近一次成功下载的新版译文。

也可以在 GMCM 中打开「GMCM 配置文本本地化」并点击「同步社区翻译」，或在 SMAPI 控制台运行 `gmcmtext_sync`。同步后重新打开对应设置页即可查看当前语言的译文。

翻译优先级依次为目标模组自带译文、本地 `overrides`、社区翻译包、原文。

## 本地翻译与投稿

运行 `gmcmtext_export zh` 导出缺失的中文翻译，也可附加模组 ID 只检查指定模组。模板写入模组目录的 `overrides/zh/`。左边是原文，在右边的双引号里填写译文。一个模组里相同的原文只需翻译一次。

```json
{
  "Enable mod": "启用模组",
  "Fish quality": ""
}
```

重新导出会保留已填写的译文。保存后重新打开 GMCM 页面即可查看译文。

导出时会重新读取 GMCM 的文字回调。查找目标模组的 i18n 时会识别常见的缩进、箭头和已填入的参数。多个键给出同一译文时可直接使用。若译文存在冲突，会继续查找备用翻译。

从本机发布译文时，在仓库根目录运行 `python publish-translations.py`。脚本会从游戏 Mods 目录的 `overrides` 读取新增和修订的译文，校验后只提交仓库 `overrides/` 中涉及的文件，并推送到 `origin/main`。脚本会跳过空译文和标记为「无需翻译」的条目。添加 `--dry-run` 可以预览。游戏装在其他位置时，把游戏内的 `overrides` 完整路径作为参数传入。

要分享翻译，fork 本仓库，将填写好的 JSON 放在 `overrides/<语言代码>/<模组UniqueID>.json`，例如 `overrides/zh/Bouhm.NPCMapLocations.json`。将变更推到自己的 fork，再向本仓库的 `main` 分支发起 Pull Request。若已有同语言、同模组文件，请将新译文合并到该文件。合并后，GitHub Actions 会发布翻译包，其他玩家可在发布完成后同步。[Fork 本仓库](https://github.com/iMoonDay/GMCMConfigTextLocalizer/fork)

To contribute a translation, fork this repository, add or update `overrides/<locale>/<mod UniqueID>.json` with a flat original-to-translation JSON object, and open a pull request against `main`. GitHub Actions publishes the packs to `community-packs` for in-game sync.

## 构建

```powershell
dotnet build GMCMConfigTextLocalizer.csproj -c Release -p:GamePath="E:/SteamLibrary/steamapps/common/Stardew Valley"
```
