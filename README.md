# ChatGPT 个性化启动

Windows 便携工具。默认从官方已注册的 ChatGPT/Codex 入口启动，WPF 界面播放启动动画。需要换肤时，可在设置中启用本机调试接口。实际支持取决于安装的 Microsoft Store 版本。

## 使用

下载 GitHub Releases 中的 Windows x64 ZIP，将整个压缩包解压到同一个文件夹。运行前请安装 .NET 10 Desktop Runtime（x64）和 Microsoft Store 版 ChatGPT/Codex。

发布包不附带视频或背景图片。启动动画和视频背景均使用你在设置里选择的本机素材。

1. 双击解压目录中的 `CodexStart.exe`。第一次会打开个性化设置。点「创建设置快捷方式」后，可以双击「ChatGPT 启动设置」随时回来修改配置。也可以运行 `CodexStart.exe --settings`。
2. 在「分区背景 → 整窗」切换「图片」或「视频」。支持本机 PNG、JPEG 和不超过 128 MB 的 H.264 MP4。两种素材的路径分别保存。视频在主窗口循环播放，默认静音，可勾选「播放视频声音」。窗口最小化时暂停，恢复后接着播放。各区可沿用整窗背景，或选自己的静态图片。
3. 在「启动动画」自行搭配开场、等待、揭幕三段素材，或选择一条完整视频并标记两个时间点。建议使用 H.264 MP4，视频静音播放。等待段也支持静态照片。在预览里点「模拟就绪」可以检查等待循环和揭幕衔接。没有可用素材时直接打开 Codex。
4. 点「创建启动快捷方式」并保存。新建快捷方式默认名为 `ChatGPT`，使用 Windows 中已注册的官方应用图标。之后用该入口启动官方客户端。

冷启动默认按视频比例调整窗口，选「全屏启动」则最大化并保留任务栏。也可以用「ChatGPT 全屏启动」快捷方式直接按最大化模式启动。视频第一帧从桌面淡入，Codex 在播放期间启动。视频等比铺满播放区域，比例不同时从画面中央取景。等待段会循环到 Codex 的输入框可用，或出现可操作的登录入口。时间轴模式会播完当前一轮再接揭幕段。播放区域与 Codex 的可见窗口四边对齐后，动画才会淡出。已经运行时，普通入口直接切回官方窗口。动画结束后启动器退出。已启用换肤时，后台换肤进程在官方客户端完整退出时一起结束。换肤进程不创建托盘图标。如果官方客户端收起窗口后仍在后台运行，换肤进程会继续工作。

## 换肤兼容性

- 「对官方 Codex 启用本机换肤」默认关闭。启用后从自定义入口冷启动，使用独立的本机客户端配置，首次可能需要登录。
- 本机调试端口只使用回环地址，同机程序仍可能访问。完全退出官方客户端会关闭该端口。程序文件和签名由官方客户端管理。
- 若兼容性检查失败，官方窗口仍会打开。之后可在个性化设置查看上次启动状态。未启用换肤的窗口要完整退出后从自定义入口启动，才能加载新主题。

## 构建与检查

需要 .NET 10 SDK 和 Windows Desktop Runtime。项目使用本机引用包。

```powershell
dotnet build .\CodexStart.csproj -c Release --configfile .\NuGet.Config
dotnet publish .\CodexStart.csproj -c Release --no-restore -o .\dist
dotnet build .\tests\Smoke.csproj -c Release --configfile .\NuGet.Config
& .\tests\bin\Release\net10.0-windows\CodexStart.Smoke.exe .\tests\obj
node --test .\tests\wallpaper.test.cjs
```

设计参考 [Codex Dream Skin](https://github.com/Fei-Away/Codex-Dream-Skin)、[Codex Dynamic Skin](https://github.com/CCDawn/Codex-Dynamic-Skin) 和 [VS Code Background](https://github.com/shalldie/vscode-background)。本项目和这些社区工具均为第三方工具。
