Unicode true

!include "MUI2.nsh"

!ifndef APP_VERSION
  !define APP_VERSION "2.14.3.0"
!endif

!ifndef PUBLISH_DIR
  !define PUBLISH_DIR "${__FILEDIR__}\UWPHook\bin\Release\net8.0-windows\win-x64\publish"
!endif

!ifndef INSTALLER_OUT
  !define INSTALLER_OUT "${__FILEDIR__}\artifacts\UWPHook-${APP_VERSION}-win-x64-setup.exe"
!endif

!define APP_NAME "UWPHook"
!define COMPANY_NAME "UWPHook contributors"
!define WEB_SITE "https://github.com/acabtr/UWPHook"
!define DESCRIPTION "The easy way to add UWP and Xbox Game Pass games to Steam"
!define MAIN_APP_EXE "UWPHook.exe"
!define REG_ROOT HKCU
!define REG_APP_PATH "Software\Microsoft\Windows\CurrentVersion\App Paths\${MAIN_APP_EXE}"
!define UNINSTALL_PATH "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_NAME}"

Name "${APP_NAME}"
Caption "${APP_NAME} Setup"
OutFile "${INSTALLER_OUT}"
InstallDir "$LOCALAPPDATA\Programs\${APP_NAME}"
RequestExecutionLevel user
SetCompressor /SOLID lzma
BrandingText "${APP_NAME}"

VIProductVersion "${APP_VERSION}"
VIAddVersionKey "ProductName" "${APP_NAME}"
VIAddVersionKey "CompanyName" "${COMPANY_NAME}"
VIAddVersionKey "LegalCopyright" "Copyright 2020-2026 UWPHook contributors"
VIAddVersionKey "FileDescription" "${DESCRIPTION}"
VIAddVersionKey "FileVersion" "${APP_VERSION}"
VIAddVersionKey "ProductVersion" "${APP_VERSION}"

!define MUI_ABORTWARNING
!define MUI_ICON "${__FILEDIR__}\UWPHook\Resources\hook2.ico"
!define MUI_UNICON "${__FILEDIR__}\UWPHook\Resources\hook2.ico"
!define MUI_FINISHPAGE_RUN "$INSTDIR\${MAIN_APP_EXE}"

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_LICENSE "${__FILEDIR__}\License.md"
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_UNPAGE_FINISH

!insertmacro MUI_LANGUAGE "English"

Section "Install" SEC_INSTALL
  SetShellVarContext current
  SetOutPath "$INSTDIR"
  SetOverwrite ifnewer
  File "${PUBLISH_DIR}\${MAIN_APP_EXE}"

  WriteUninstaller "$INSTDIR\uninstall.exe"

  CreateDirectory "$SMPROGRAMS\${APP_NAME}"
  CreateShortCut "$SMPROGRAMS\${APP_NAME}\${APP_NAME}.lnk" "$INSTDIR\${MAIN_APP_EXE}"
  CreateShortCut "$SMPROGRAMS\${APP_NAME}\Uninstall ${APP_NAME}.lnk" "$INSTDIR\uninstall.exe"
  CreateShortCut "$DESKTOP\${APP_NAME}.lnk" "$INSTDIR\${MAIN_APP_EXE}"

  WriteRegStr ${REG_ROOT} "${REG_APP_PATH}" "" "$INSTDIR\${MAIN_APP_EXE}"
  WriteRegStr ${REG_ROOT} "${UNINSTALL_PATH}" "DisplayName" "${APP_NAME}"
  WriteRegStr ${REG_ROOT} "${UNINSTALL_PATH}" "DisplayVersion" "${APP_VERSION}"
  WriteRegStr ${REG_ROOT} "${UNINSTALL_PATH}" "DisplayIcon" "$INSTDIR\${MAIN_APP_EXE}"
  WriteRegStr ${REG_ROOT} "${UNINSTALL_PATH}" "Publisher" "${COMPANY_NAME}"
  WriteRegStr ${REG_ROOT} "${UNINSTALL_PATH}" "URLInfoAbout" "${WEB_SITE}"
  WriteRegStr ${REG_ROOT} "${UNINSTALL_PATH}" "UninstallString" '$\"$INSTDIR\uninstall.exe$\"'
  WriteRegStr ${REG_ROOT} "${UNINSTALL_PATH}" "QuietUninstallString" '$\"$INSTDIR\uninstall.exe$\" /S'
  WriteRegDWORD ${REG_ROOT} "${UNINSTALL_PATH}" "NoModify" 1
  WriteRegDWORD ${REG_ROOT} "${UNINSTALL_PATH}" "NoRepair" 1
SectionEnd

Section "Uninstall"
  SetShellVarContext current
  Delete "$DESKTOP\${APP_NAME}.lnk"
  Delete "$SMPROGRAMS\${APP_NAME}\${APP_NAME}.lnk"
  Delete "$SMPROGRAMS\${APP_NAME}\Uninstall ${APP_NAME}.lnk"
  RMDir "$SMPROGRAMS\${APP_NAME}"

  DeleteRegKey ${REG_ROOT} "${REG_APP_PATH}"
  DeleteRegKey ${REG_ROOT} "${UNINSTALL_PATH}"

  RMDir /r "$INSTDIR"
SectionEnd
