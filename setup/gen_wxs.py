# -*- coding: utf-8 -*-
"""扫描 payload 目录，生成 WiX 4 的 Package.wxs（含完整目录树与文件清单）。

改动 payload（换图标、改源码、重发程序）后必须重新跑一次，否则安装包内容不会变。
用法：python gen_wxs.py   （由 build.cmd 自动调用）
"""
import io
import os

ROOT = "payload"
OUT = "Package.wxs"
UPGRADE_CODE = "68035F95-AAE6-46DF-82A9-1D26ADEC7A9F"   # 固定不变，升级安装靠它识别


def main():
    files = []
    for dp, _, ns in os.walk(ROOT):
        for n in ns:
            files.append(os.path.join(dp, n).replace("\\", "/"))
    files.sort()
    if not files:
        raise SystemExit("payload 目录是空的，先执行 build.cmd 的第 1、2 步")

    dirs = set()
    for f in files:
        d = os.path.dirname(f[len(ROOT) + 1:])
        while d:
            dirs.add(d)
            d = os.path.dirname(d)
    dir_id = {d: "D%03d" % i for i, d in enumerate(sorted(dirs), 1)}

    tree = {}
    for d in dirs:
        tree.setdefault(os.path.dirname(d), []).append(d)

    def emit(parent, depth):
        out = []
        ind = "  " * depth
        for d in sorted(tree.get(parent, [])):
            if tree.get(d):
                out.append('%s<Directory Id="%s" Name="%s">' % (ind, dir_id[d], os.path.basename(d)))
                out.extend(emit(d, depth + 1))
                out.append("%s</Directory>" % ind)
            else:
                out.append('%s<Directory Id="%s" Name="%s" />' % (ind, dir_id[d], os.path.basename(d)))
        return out

    L = []
    L.append('<?xml version="1.0" encoding="utf-8"?>')
    L.append('<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs"')
    L.append('     xmlns:ui="http://wixtoolset.org/schemas/v4/wxs/ui">')
    L.append("")
    L.append('  <Package Name="WeatherPanel 桌面天气面板"')
    L.append('           Manufacturer="WeatherPanel" Version="1.0.0"')
    L.append('           UpgradeCode="%s"' % UPGRADE_CODE)
    L.append('           Scope="perUser" Compressed="yes" Language="2052" Codepage="65001">')
    L.append("")
    L.append('    <SummaryInformation Description="Windows 桌面常驻天气面板（深色玻璃材质，中国天气网 + Open-Meteo）" />')
    L.append('    <MajorUpgrade DowngradeErrorMessage="已安装更新版本，请先在「设置 → 应用」中卸载后再安装。" />')
    L.append('    <MediaTemplate EmbedCab="yes" CompressionLevel="high" />')
    L.append("")
    L.append('    <StandardDirectory Id="LocalAppDataFolder">')
    L.append('      <Directory Id="ProgramsFolder" Name="Programs">')
    L.append('        <Directory Id="INSTALLFOLDER" Name="WeatherPanel">')
    L.extend(emit("", 5))
    L.append("        </Directory>")
    L.append("      </Directory>")
    L.append("    </StandardDirectory>")
    L.append("")
    L.append('    <ui:WixUI Id="WixUI_InstallDir" InstallDirectory="INSTALLFOLDER" />')
    L.append("")
    L.append('    <Feature Id="Main" Title="WeatherPanel 天气面板" Level="1">')
    L.append('      <ComponentGroupRef Id="AppPayload" />')
    L.append('      <ComponentGroupRef Id="AppShortcuts" />')
    L.append("    </Feature>")
    L.append("  </Package>")
    L.append("")

    # 每个文件独立 Component（多文件组件无法用自动 GUID）
    L.append("  <Fragment>")
    L.append('    <ComponentGroup Id="AppPayload">')
    idx = 0
    for f in files:
        d = os.path.dirname(f[len(ROOT) + 1:])
        did = dir_id.get(d, "INSTALLFOLDER") if d else "INSTALLFOLDER"
        idx += 1
        L.append('      <Component Directory="%s" Guid="*">' % did)
        L.append('        <File Id="f%04d" Source="%s" />' % (idx, f))
        L.append("      </Component>")
    L.append("    </ComponentGroup>")
    L.append("  </Fragment>")
    L.append("")

    L.append("  <Fragment>")
    L.append('    <ComponentGroup Id="AppShortcuts">')
    L.append('      <Component Id="StartMenuShortcut" Directory="ProgramMenuFolder" Guid="8C1B4E64-3A2D-4E2B-9D71-6F0E5B2C7A31">')
    L.append('        <Shortcut Id="StartMenuLink" Name="WeatherPanel 天气面板" Description="桌面天气面板"')
    L.append('                  Target="[INSTALLFOLDER]WeatherPanel.exe" WorkingDirectory="INSTALLFOLDER" />')
    L.append('        <RemoveFolder Id="RemoveStartMenuFolder" Directory="ProgramMenuFolder" On="uninstall" />')
    L.append('        <RegistryValue Root="HKCU" Key="Software\\WeatherPanel" Name="StartMenu" Type="integer" Value="1" KeyPath="yes" />')
    L.append("      </Component>")
    L.append('      <Component Id="DesktopShortcut" Directory="DesktopFolder" Guid="2F7A9C13-5B84-4D6E-B0C2-84E1D3A6F905">')
    L.append('        <Shortcut Id="DesktopLink" Name="WeatherPanel 天气面板" Description="桌面天气面板"')
    L.append('                  Target="[INSTALLFOLDER]WeatherPanel.exe" WorkingDirectory="INSTALLFOLDER" />')
    L.append('        <RegistryValue Root="HKCU" Key="Software\\WeatherPanel" Name="DesktopIcon" Type="integer" Value="1" KeyPath="yes" />')
    L.append("      </Component>")
    L.append('      <Component Id="AutoStartEntry" Directory="INSTALLFOLDER" Guid="9E4D2B76-1C58-4A3F-8B20-7D6F1C9E4A53">')
    L.append('        <RegistryValue Root="HKCU" Key="Software\\Microsoft\\Windows\\CurrentVersion\\Run" Name="WeatherPanel"')
    L.append('                       Type="string" Value="&quot;[INSTALLFOLDER]WeatherPanel.exe&quot;" KeyPath="yes" />')
    L.append("      </Component>")
    L.append("    </ComponentGroup>")
    L.append("  </Fragment>")
    L.append("")
    L.append("</Wix>")

    io.open(OUT, "w", encoding="utf-8", newline="\r\n").write("\n".join(L))
    print("Package.wxs 已生成：%d 个文件 / %d 个目录" % (len(files), len(dirs)))


if __name__ == "__main__":
    main()
