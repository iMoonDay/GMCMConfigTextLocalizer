# GMCM 配置文本本地化（GMCM Config Text Localizer）

为 GMCM 配置界面中的选项名称、说明、分组文字、段落、分页标题和下拉选项查找翻译。模组会优先使用目标模组自己的 i18n 翻译，并在找不到时使用本模组的覆盖文件。

## 同步翻译

安装 SMAPI 4.1 或更高版本、GMCM 1.13 或更高版本，再把下载包中的模组目录放到游戏的 `Mods` 目录。由旧版本升级时，把旧目录中的 `overrides` 移到新目录，然后从 `Mods` 移走旧版本。[下载安装包](https://github.com/iMoonDay/GMCMConfigTextLocalizer/releases/latest)

在 GMCM 中打开「GMCM 配置文本本地化」，点击「同步社区翻译」。也可以在 SMAPI 控制台运行 `gmcmtext_sync`。模组会从 GitHub 主分支下载全部语言的翻译包，按游戏当前语言使用。同步后重新打开对应设置页即可查看译文。最近一次成功下载的译文会缓存下来，可离线使用。

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

要分享翻译，fork 本仓库，将填写好的 JSON 放在 `packs/<语言代码>/<模组UniqueID>.json`，例如 `packs/zh/Bouhm.NPCMapLocations.json`。将变更推到自己的 fork，再向本仓库的 `main` 分支发起 Pull Request。若已有同语言、同模组文件，请将新译文合并到该文件。合并后，其他玩家下次同步即可获取。[Fork 本仓库](https://github.com/iMoonDay/GMCMConfigTextLocalizer/fork)

To contribute a translation, fork this repository, add or update `packs/<locale>/<mod UniqueID>.json` with a flat original-to-translation JSON object, and open a pull request against `main`. In-game sync downloads all packs from that branch.

## 构建

```powershell
dotnet build GMCMConfigTextLocalizer.csproj -c Release -p:GamePath="E:/SteamLibrary/steamapps/common/Stardew Valley"
```
