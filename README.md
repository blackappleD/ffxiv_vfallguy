# ffxiv_vfallguy dalamud plugin

> 本仓库是 [awgil/ffxiv_vfallguy](https://github.com/awgil/ffxiv_vfallguy) 的国服维护版（InternalName 保持 `vfallguy`，请勿与原版同时安装）。
>
> 插件仓库地址：`https://raw.githubusercontent.com/blackappleD/DalamudPlugins/main/repo.json`
>
> 国服维护版新增：
> - **获得金碟声誉后立即退出**：聊天栏出现「获得了N个金碟声誉」时自动退出副本（默认开启，可在窗口中关闭）。
> - **金碟声誉统计**：记录每次获得的金碟声誉，显示累计获得、次数、统计时长和平均每小时获得量；数据会保存，可按住 Ctrl 点击「重置统计」清零。
>   统计时长只计算在副本内、或在大厅中排队/开启 Auto register 的时间，在大厅闲置不计入。
>- **金碟声誉自动购物**（需要 GatherBuddy Reborn 国服维护版 IPC 5+ 和 vnavmesh）：在「金碟声誉自动购物」中直接勾选兑换物品并设置目标持有数量，
>   开启 Auto register 后，在大厅中金碟声誉达到阈值（上限 10000）时自动把清单写入 GBR 购买，买完走回节目登记员继续报名。已学习的物品会自动跳过。
>   达到阈值但没有需要购买的物品（未勾选、已学习、已达目标数量或买不起）时跳过购物，继续自动报名；购物流程出错时会关闭 Auto register 并在聊天栏提示。
> - **刷满后停止自动报名**（默认开启）：金碟声誉已满，或剩余空间不足一次获得量（按最近记录的最大单次获得量估算）时，在下一次报名前关闭 Auto register。
>
> 版本规则：上游第 4 段 ×1000 + 本地修订（上游 0.0.0.12 → 0.0.0.12001 起）。发布方式：本地 `dotnet build -c Release`，
> 将 `vfallguy/bin/Release/vfallguy/latest.zip` 复制为 `vfallguy.zip` 后用 `gh release create v<版本号>` 上传。
vFallguy dalamud plugin aims to make the Fall Guys Collaboration event in FFXIV less of a hassle

Dalamud repository:
```
https://puni.sh/api/repository/veyn
```

# How to install

1) Open the Dalamud Settings menu in-game
2) Under Custom Plugin Repositories, enter ```https://puni.sh/api/repository/veyn``` into the empty box at the bottom
3) Click the "+" button.
4) Click the "Save and Close" button.
5) You will now be able to find the plugin in your Plugin Installer and install it

# Current list of features
Automatically registers for the Fall Guys minigame 

Automatically leaves the instance if you are not the only person in it (Useful for finding solo instances)

Built-in button to leave the instance quickly (skips the confirmation button)

Adds markers to showcase upcoming AoEs on stage 3 of the minigame
