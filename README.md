# LiquidGlass Dock

LiquidGlass Dock 是面向 Windows x64 的桌面 Dock，带应用启动台、任务栏固定项同步和实时玻璃材质。

从 [Releases](https://github.com/gysCodeLab/LiquidGlassDock/releases) 下载 Windows x64 预览包。完整解压后运行 LiquidGlass.Desktop.exe，首次试用时可先保留系统任务栏。若开启了任务栏接管，可从 Dock 菜单关闭 Dock 并恢复任务栏，也可以双击包内的 恢复任务栏.bat。更多操作见[使用说明](docs/使用说明.md)。

## 从源码构建

在 Windows x64 上安装 .NET 9 SDK，然后运行：

    dotnet publish src/LiquidGlass.Desktop/LiquidGlass.Desktop.csproj -c Release -r win-x64 --self-contained true -o publish

发布目录中的 EXE 需要和 Shaders 目录一起分发。

## v1.66-preview

修复解锁进入桌面时 Dock 短暂闪白的问题。在桌面采集恢复期间，玻璃中央直接透出当前桌面并保留边缘，收到有效桌面帧后恢复完整 F 版材质。正常状态使用原 F 版外观。

本机用户已确认解锁不再闪白。正常状态的白底与白底列表渲染与原 F 版逐像素一致。其他硬件上的解锁恢复仍待验证。