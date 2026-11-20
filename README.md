# LiquidGlass Dock

LiquidGlass Dock 是面向 Windows x64 的桌面 Dock，带应用启动台、任务栏固定项同步和实时玻璃材质。

从 GitHub Releases 下载 Windows x64 预览包。完整解压后运行 LiquidGlass.Desktop.exe，首次试用时可先保留系统任务栏。若开启了任务栏接管，可从 Dock 菜单关闭 Dock 并恢复任务栏，也可以双击包内的 恢复任务栏.bat。更多操作见[使用说明](docs/使用说明.md)。

## 从源码构建

在 Windows x64 上安装 .NET 9 SDK，然后运行：

    dotnet publish src/LiquidGlass.Desktop/LiquidGlass.Desktop.csproj -c Release -r win-x64 --self-contained true -o publish

发布目录中的 EXE 需要和 Shaders 目录一起分发。
