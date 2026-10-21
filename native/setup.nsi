Unicode true
!include "MUI2.nsh"
!include "LogicLib.nsh"
Name "FlowLog Native"
OutFile "${PACKAGE_DIR}\FlowLog-Native-0.2.0-Setup.exe"
InstallDir "$LOCALAPPDATA\Programs\FlowLog Native"
RequestExecutionLevel user
SetCompressor /SOLID lzma
VIProductVersion "0.2.0.0"
VIAddVersionKey "ProductName" "FlowLog Native"
VIAddVersionKey "FileDescription" "FlowLog Windows native installer"
VIAddVersionKey "FileVersion" "0.2.0"
VIAddVersionKey "LegalCopyright" "MIT Copyright 2026 iChenwin"
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_LICENSE "..\LICENSE"
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "SimpChinese"

Function .onInit
  ReadRegDWORD $0 HKLM "SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full" "Release"
  ${If} $0 < 528040
    MessageBox MB_OK|MB_ICONSTOP "需要 Microsoft .NET Framework 4.8。请通过 Windows 更新或微软官网安装后重试。安装包不包含此系统组件。"
    Abort
  ${EndIf}
FunctionEnd

Section "FlowLog Native"
  SetShellVarContext current
  SetOutPath "$INSTDIR"
  File "out\FlowLog.Native.exe"
  File "out\FlowLog.Native.exe.config"
  File "out\ICSharpCode.AvalonEdit.dll"
  File "out\LICENSE.txt"
  File "out\THIRD-PARTY-NOTICES.txt"
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  CreateDirectory "$SMPROGRAMS\FlowLog Native"
  CreateShortcut "$SMPROGRAMS\FlowLog Native\FlowLog Native.lnk" "$INSTDIR\FlowLog.Native.exe"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\FlowLogNative" "DisplayName" "FlowLog Native"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\FlowLogNative" "DisplayVersion" "0.2.0"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\FlowLogNative" "Publisher" "iChenwin"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\FlowLogNative" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\FlowLogNative" "UninstallString" '$\"$INSTDIR\Uninstall.exe$\"'
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\FlowLogNative" "NoModify" 1
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\FlowLogNative" "NoRepair" 1
SectionEnd

Section "Uninstall"
  SetShellVarContext current
  Delete "$INSTDIR\FlowLog.Native.exe"
  Delete "$INSTDIR\FlowLog.Native.exe.config"
  Delete "$INSTDIR\ICSharpCode.AvalonEdit.dll"
  Delete "$INSTDIR\LICENSE.txt"
  Delete "$INSTDIR\THIRD-PARTY-NOTICES.txt"
  Delete "$INSTDIR\Uninstall.exe"
  RMDir "$INSTDIR"
  Delete "$SMPROGRAMS\FlowLog Native\FlowLog Native.lnk"
  RMDir "$SMPROGRAMS\FlowLog Native"
  DeleteRegKey HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\FlowLogNative"
SectionEnd
