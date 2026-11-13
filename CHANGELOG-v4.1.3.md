# DarkBox by Hamii — v4.1.3 changelog (12 Nov 2026)

## WebView2: silent install exit 0, lekin register nahi hua

**Log se sabit (15:07–15:12):** download mukammal hua (214,990,032
bytes, ~4 minute), installer do dafa chala — elevated exit 0, phir
per-user exit 0 — lekin runtime registry mein register nahi hua.
Matlab installer "kamyab" kehta hai, Windows usay manti nahi. Is ki
do mumkin wajahen hain: (a) runtime files asal mein install ho gayi
hain, sirf registry key nahi bani — to pehchan ka nizam hi ghalat tha;
(b) installer kisi wajah se asal install rok raha hai (Edge khula hai,
pending reboot, policy) aur silent mode mein wo wajah chhupa raha hai.

**Fix (v4.1.3):**

1. **File-based pehchan** (`setup-boot/Program.cs`, `src/WebView2Setup.cs`):
   `IsWebView2Installed` ab sirf registry nahi dekhta — `msedgewebview2.exe`
   bhi dhoondhta hai (`Program Files (x86)\Microsoft\EdgeWebView\Application\<version>\`
   aur `Program Files\...` dono). Registry key bhi ab sirf mojood hone
   par hi kaafi hai (version value ka intezar nahi). Agar files mojood
   hain to DarkBox khul jaye ga — dobara download nahi hoga.
2. **Asal diagnostics log mein**: fail hone par `%TEMP%\DarkBoxSetup.log`
   mein ab ye likha hota hai — EdgeWebView ke kaun se folders/version
   disk par hain, `msedgewebview2.exe` hai ya nahi, aur registry key
   har hive/view mein mojood hai ya nahi (pv ke saath). Agli dafa log
   bhejne par asal wajah seedhi nazar aaye gi.
3. **Visible installer, aakhri koshish**: dono silent koshishen nakam
   hon to Microsoft ka installer aik dafa **khula** chalaya jata hai
   (bina `/silent`), taake uski apni screen/ghalti nazar aaye — masalan
   "Please close Microsoft Edge" ya koi policy error. Status window
   par pehle se likha hota hai ke installer khula chal raha hai.

## Build (verified on this VM, .NET SDK 8.0.428 at ~/.dotnet)
```bat
dotnet build -c Release
copy bin\Release\net48\HamiiFolderLocker.exe pkg\HamiiFolderLocker\
mkdir build-payload && copy pkg\HamiiFolderLocker\* build-payload\
(cd build-payload && zip ../setup-boot/payload.zip *)
dotnet build setup-boot/DarkBoxSetup.csproj -c Release
```
