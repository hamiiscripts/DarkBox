; DarkBox by Hamii — per-user installer (no admin rights needed)
Unicode true
!include "MUI2.nsh"

Name "DarkBox by Hamii"
OutFile "DarkBox-Setup.exe"
InstallDir "$LOCALAPPDATA\Programs\HamiiFolderLocker"
InstallDirRegKey HKCU "Software\HamiiFolderLocker" "InstallDir"
RequestExecutionLevel user
SetCompressor /SOLID lzma
BrandingText "Hamii Scripts"
!define MUI_ICON "app.ico"
!define MUI_UNICON "app.ico"
!define MUI_ABORTWARNING
!define MUI_WELCOMEPAGE_TITLE "Welcome to DarkBox Setup"
!define MUI_WELCOMEPAGE_TEXT "This will install DarkBox 3.4.0 on your computer.$\r$\n$\r$\nDarkBox protects your folders with a pattern or passcode — they lock themselves again the moment you close them.$\r$\n$\r$\nClick Next to continue."
!define MUI_FINISHPAGE_RUN "$INSTDIR\HamiiFolderLocker.exe"
!define MUI_FINISHPAGE_RUN_TEXT "Launch DarkBox"
!define MUI_FINISHPAGE_TITLE "Setup Complete"
!define MUI_FINISHPAGE_TEXT "DarkBox has been installed.$\r$\n$\r$\nShortcuts are on your Desktop and in the Start Menu. Your existing locker setup (pattern, passcode, folders) is kept automatically."

VIProductVersion "3.4.0.0"
VIAddVersionKey "ProductName" "DarkBox"
VIAddVersionKey "CompanyName" "Hamii Scripts"
VIAddVersionKey "FileDescription" "DarkBox Setup"
VIAddVersionKey "FileVersion" "3.4.0.0"
VIAddVersionKey "LegalCopyright" "Hamii Scripts"

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "English"

Section "Install"
  nsExec::Exec 'taskkill /F /IM HamiiFolderLocker.exe'
  Pop $0
  SetOutPath "$INSTDIR"
  File "pkg\HamiiFolderLocker\HamiiFolderLocker.exe"
  File "pkg\HamiiFolderLocker\HamiiFolderLocker.exe.config"
  File "pkg\HamiiFolderLocker\Microsoft.Web.WebView2.Core.dll"
  File "pkg\HamiiFolderLocker\Microsoft.Web.WebView2.WinForms.dll"
  File "pkg\HamiiFolderLocker\WebView2Loader.dll"
  File "pkg\HamiiFolderLocker\DirectShowLib.dll"
  File "pkg\HamiiFolderLocker\README.md"
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  ; shortcuts: Start Menu root (best for Windows Search), Start Menu
  ; folder, and Desktop
  CreateShortcut "$SMPROGRAMS\DarkBox.lnk" "$INSTDIR\HamiiFolderLocker.exe"
  CreateDirectory "$SMPROGRAMS\DarkBox"
  CreateShortcut "$SMPROGRAMS\DarkBox\DarkBox.lnk" "$INSTDIR\HamiiFolderLocker.exe"
  CreateShortcut "$SMPROGRAMS\DarkBox\Uninstall.lnk" "$INSTDIR\Uninstall.exe"
  CreateShortcut "$DESKTOP\DarkBox.lnk" "$INSTDIR\HamiiFolderLocker.exe"
  ; App Paths: lets Windows find "DarkBox" from Search / Run too
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\App Paths\DarkBox.exe" "" "$INSTDIR\HamiiFolderLocker.exe"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\App Paths\DarkBox.exe" "Path" "$INSTDIR"
  ; remove the pre-rename (Hamii Folder Locker) shortcuts, if any
  Delete "$SMPROGRAMS\Hamii Folder Locker\Hamii Folder Locker.lnk"
  Delete "$SMPROGRAMS\Hamii Folder Locker\Uninstall.lnk"
  RMDir "$SMPROGRAMS\Hamii Folder Locker"
  Delete "$DESKTOP\Hamii Folder Locker.lnk"
  WriteRegStr HKCU "Software\HamiiFolderLocker" "InstallDir" "$INSTDIR"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\HamiiFolderLocker" "DisplayName" "DarkBox by Hamii"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\HamiiFolderLocker" "DisplayVersion" "3.4.0"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\HamiiFolderLocker" "Publisher" "Hamii Scripts"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\HamiiFolderLocker" "DisplayIcon" "$INSTDIR\HamiiFolderLocker.exe"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\HamiiFolderLocker" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\HamiiFolderLocker" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\HamiiFolderLocker" "NoModify" 1
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\HamiiFolderLocker" "NoRepair" 1
SectionEnd

Section "Uninstall"
  nsExec::Exec 'taskkill /F /IM HamiiFolderLocker.exe'
  Pop $0
  Delete "$INSTDIR\HamiiFolderLocker.exe"
  Delete "$INSTDIR\HamiiFolderLocker.exe.config"
  Delete "$INSTDIR\Microsoft.Web.WebView2.Core.dll"
  Delete "$INSTDIR\Microsoft.Web.WebView2.WinForms.dll"
  Delete "$INSTDIR\WebView2Loader.dll"
  Delete "$INSTDIR\DirectShowLib.dll"
  Delete "$INSTDIR\README.md"
  Delete "$INSTDIR\Uninstall.exe"
  RMDir "$INSTDIR"
  Delete "$SMPROGRAMS\DarkBox.lnk"
  Delete "$SMPROGRAMS\DarkBox\DarkBox.lnk"
  Delete "$SMPROGRAMS\DarkBox\Uninstall.lnk"
  RMDir "$SMPROGRAMS\DarkBox"
  Delete "$DESKTOP\DarkBox.lnk"
  Delete "$SMPROGRAMS\Hamii Folder Locker\Hamii Folder Locker.lnk"
  Delete "$SMPROGRAMS\Hamii Folder Locker\Uninstall.lnk"
  RMDir "$SMPROGRAMS\Hamii Folder Locker"
  Delete "$DESKTOP\Hamii Folder Locker.lnk"
  DeleteRegKey HKCU "Software\Microsoft\Windows\CurrentVersion\App Paths\DarkBox.exe"
  DeleteRegKey HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\HamiiFolderLocker"
  DeleteRegKey HKCU "Software\HamiiFolderLocker"
  ; Note: locker data in %APPDATA%\HamiiFolderLockerPro is kept on purpose.
SectionEnd
