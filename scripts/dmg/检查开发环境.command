#!/bin/zsh
# Shipped in the installer: checks what Emulator Shell needs, the same way the app looks for it (Sdk.swift),
# and says how to install what is missing. Double-click it in Finder; it opens in Terminal.

autoload -U colors && colors
ok()   { print -P "  %F{green}✓%f $1" }
bad()  { print -P "  %F{red}✗%f $1"; [[ -n ${2:-} ]] && print -P "    %F{8}→ $2%f"; problems=$((problems + 1)) }
note() { print -P "  %F{yellow}•%f $1"; [[ -n ${2:-} ]] && print -P "    %F{8}→ $2%f" }
section() { print; print -P "%B$1%b" }
problems=0

clear
print -P "%B%F{green}Emulator Shell%f · 开发环境检查%b"

section "Emulator Shell"
app=""
for candidate in "/Applications/Emulator Shell.app" "$HOME/Applications/Emulator Shell.app"; do
    [[ -d $candidate ]] && app=$candidate && break
done
if [[ -n $app ]]; then
    ok "已安装：${app/#$HOME/~}"
else
    bad "还没有安装" "把安装窗口里的 Emulator Shell 拖到「应用程序」"
fi

section "Android SDK"
sdk=${ANDROID_HOME:-${ANDROID_SDK_ROOT:-$HOME/Library/Android/sdk}}
sdk=${~sdk}
if [[ -d $sdk ]]; then
    ok "SDK：${sdk/#$HOME/~}"
    if [[ -x $sdk/emulator/emulator.real ]]; then
        ok "模拟器：$($sdk/emulator/emulator.real -version 2>/dev/null | head -1 | sed 's/^Android emulator version //')"
        ok "Android Studio、Flutter 已经通过 Emulator Shell 打开模拟器"
    elif [[ -x $sdk/emulator/emulator ]]; then
        ok "模拟器：$($sdk/emulator/emulator -version 2>/dev/null | head -1 | sed 's/^Android emulator version //')"
        note "想让 Android Studio、Flutter 也用 Emulator Shell 打开模拟器？" \
             "打开 app，菜单 Emulator Shell › 让 Android Studio 等工具使用 Emulator Shell"
    else
        bad "没有模拟器（emulator）" "Android Studio › Settings › Android SDK › SDK Tools，勾选 Android Emulator"
    fi
    if [[ -x $sdk/platform-tools/adb ]]; then
        ok "adb：$($sdk/platform-tools/adb version 2>/dev/null | head -1 | sed 's/^Android Debug Bridge version //')"
    else
        bad "没有 platform-tools（adb），连接手机需要它" "app 的「物理设备」页可以一键安装，或在 SDK Tools 里勾选 Android SDK Platform-Tools"
    fi
    if [[ -x $sdk/cmdline-tools/latest/bin/avdmanager ]]; then
        ok "命令行工具（avdmanager、sdkmanager）"
    else
        bad "没有 cmdline-tools，新建设备和下载系统镜像需要它" "SDK Tools 里勾选 Android SDK Command-line Tools (latest)"
    fi
else
    bad "找不到 Android SDK（${sdk/#$HOME/~}）" "安装 Android Studio（https://developer.android.com/studio），或设置 ANDROID_HOME"
fi

section "Java（新建设备、下载镜像时用）"
java_home=${JAVA_HOME:-}
if [[ -z $java_home ]]; then
    for candidate in "/Applications/Android Studio.app/Contents/jbr/Contents/Home" \
                     "/opt/homebrew/opt/openjdk@17/libexec/openjdk.jdk/Contents/Home" \
                     "/opt/homebrew/opt/openjdk/libexec/openjdk.jdk/Contents/Home"; do
        [[ -d $candidate ]] && java_home=$candidate && break
    done
fi
[[ -z $java_home ]] && java_home=$(/usr/libexec/java_home 2>/dev/null)
if [[ -n $java_home && -x $java_home/bin/java ]]; then
    ok "$("$java_home/bin/java" -version 2>&1 | head -1)"
else
    bad "找不到 Java" "装了 Android Studio 就自带；或者 brew install openjdk@17"
fi

section "虚拟设备（AVD）"
avd_home=${ANDROID_AVD_HOME:-$HOME/.android/avd}
case $(uname -m) in arm64) mine=arm64 ;; *) mine=x86 ;; esac
count=0; foreign=()
for ini in $avd_home/*.ini(N); do
    name=${ini:t:r}
    folder=$(sed -n 's/^path=//p' $ini | head -1); [[ -d $folder ]] || folder=$avd_home/$name.avd
    abi=$(sed -n 's/^abi.type *= *//p' $folder/config.ini 2>/dev/null | head -1)
    count=$((count + 1))
    if [[ -n $abi && $abi != ${mine}* ]]; then
        foreign+=("$name（$abi）")
    else
        ok "$name  %F{8}${abi:-未知镜像}%f"
    fi
done
for f in $foreign; do
    bad "$f 是别的处理器的镜像，这台 Mac 跑不了" "在 app 里新建一台 ${mine} 的设备"
done
(( count == 0 )) && note "还没有虚拟设备" "打开 app，点「+」新建一台"

section "连接的手机和平板"
if [[ -x ${sdk:-}/platform-tools/adb ]]; then
    devices=("${(@f)$($sdk/platform-tools/adb devices -l 2>/dev/null | sed 1d | grep -v '^emulator-' | grep -v '^$')}")
    if (( ${#devices} == 0 )) || [[ -z ${devices[1]} ]]; then
        note "现在没有连着的手机" "用 USB 连上并打开 USB 调试，或在 app 里用 Wi-Fi 配对"
    else
        for line in $devices; do
            serial=${line%% *}
            model=$(print -r -- $line | sed -n 's/.*model:\([^ ]*\).*/\1/p' | tr _ ' ')
            case $line in
                *unauthorized*) note "$serial 等待授权" "在手机上点「允许 USB 调试」" ;;
                *offline*)      note "$serial 离线" "拔掉重插，或在手机上撤销 USB 调试授权后重连" ;;
                *)              ok "${model:-$serial}  %F{8}$serial · 可以投屏、截图、录屏%f" ;;
            esac
        done
    fi
else
    note "需要 adb 才能看到手机"
fi

print
if (( problems == 0 )); then
    print -P "%B%F{green}一切就绪。%f%b"
else
    print -P "%B有 $problems 项需要处理，%b按上面的提示装好后再运行一次。"
fi
if [[ -n $app ]]; then
    print
    read -k 1 "reply?按 回车 打开 Emulator Shell，其他键退出 "
    print
    [[ $reply == $'\n' ]] && open "$app"
fi
