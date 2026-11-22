# LiquidGlass Dock

LiquidGlass Dock 是面向 Windows x64 的桌面 Dock，带应用启动台、任务栏固定项同步和实时玻璃材质。

从 [Releases](https://github.com/gysCodeLab/LiquidGlassDock/releases) 下载 Windows x64 预览包。完整解压后运行 LiquidGlass.Desktop.exe，首次试用时可先保留系统任务栏。若开启了任务栏接管，可从 Dock 菜单关闭 Dock 并恢复任务栏，也可以双击包内的 恢复任务栏.bat。更多操作见[使用说明](docs/使用说明.md)。

## 显示和隐藏 Dock

在 Dock 设置中选择或录制显示／隐藏快捷键。默认是双击 Dock 玻璃空白处。

**鼠标手势和键盘组合都要求鼠标指针位于 Dock 区域内。** 使用键盘组合时，先将鼠标移到 Dock 上，再按设置的组合键，例如自己录制的 `Ctrl + M`。

Dock 隐藏后，将鼠标移回屏幕底部原来 Dock 所在的区域，再使用同一个快捷键或手势即可唤回。想查看或更改绑定，在 Dock 上点右键打开设置，找到「显示 / 隐藏 Dock 快捷键」。

## 从源码构建

在 Windows x64 上安装 .NET 9 SDK，然后运行：

    dotnet publish src/LiquidGlass.Desktop/LiquidGlass.Desktop.csproj -c Release -r win-x64 --self-contained true -o publish

发布目录中的 EXE 需要和 Shaders 目录一起分发。

## v1.67-preview

启动台沿用 Dock 的 F 版玻璃，圆角、折射边宽、折射强度与模糊随 Dock 设置同步。

## v1.66-preview

修复解锁进入桌面时 Dock 短暂闪白的问题。在桌面采集恢复期间，玻璃中央直接透出当前桌面并保留边缘，收到有效桌面帧后恢复完整 F 版材质。正常状态使用原 F 版外观。

本机用户已确认解锁不再闪白。正常状态的白底与白底列表渲染与原 F 版逐像素一致。其他硬件上的解锁恢复仍待验证。
