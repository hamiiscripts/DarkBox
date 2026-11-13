# DarkBox by Hamii — v4.1.6 changelog (12 Nov 2026)

## Wizard staging folder se chalta hai — runtime wahan nahi tha

**Screenshots se sabit (16:12):** `WebView2Runtime` folder theek jagah
par hai (`%LOCALAPPDATA%\Programs\HamiiFolderLocker\WebView2Runtime`,
andar `msedgewebview2.exe` mojood) aur `.cab` bhi Downloads mein
mojood hai — lekin DarkBox phir bhi "Download it now?" pooch raha
tha. Do wajahen nikli:

1. Installed app purani hai (`HamiiFolderLocker.exe` 612 KB, 11:10 AM)
   — use fixed-version folder ka pata hi nahi.
2. Setup ka installer wizard payload ko `%LOCALAPPDATA%\DarkBoxSetup\staging`
   mein nikaal kar wahin se chalata hai. Wizard app ke saath wali
   folder (`staging`) aur app-data mein runtime dhoondhta tha — install
   dir mein nahi. Is liye wizard khud bhi nahi khul saka, aur purani
   app wahi prompt dikhati rahi.

**Fix (v4.1.6, `src/WebView2Setup.cs`):** `GetFixedVersionFolder()`
ab teesri jagah bhi dekhta hai — install dir
(`%LOCALAPPDATA%\Programs\HamiiFolderLocker\WebView2Runtime`). Ab
wizard staging se chal kar bhi runtime dhoondh leta hai, khul jata
hai, aur nayi app install kar deta hai — jo phir apne saath wali
folder se runtime use karti hai.

**User ko kya karna hai:** bas v4.1.6 setup chalana hai. Is dafa
wizard khule ga (runtime pehle se mojood hai, dobara copy/download
kuch nahi hoga) aur nayi app install ho jaye gi.

## Build (verified on this VM, .NET SDK 8.0.428 at ~/.dotnet)
```bat
dotnet build -c Release
copy bin\Release\net48\HamiiFolderLocker.exe pkg\HamiiFolderLocker\
mkdir build-payload && copy pkg\HamiiFolderLocker\* build-payload\
(cd build-payload && zip ../setup-boot/payload.zip *)
dotnet build setup-boot/DarkBoxSetup.csproj -c Release
```
