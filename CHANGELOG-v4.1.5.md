# DarkBox by Hamii — v4.1.5 changelog (12 Nov 2026)

## Extracted fixed-version folder bhi chalta hai

**Jo hua:** user ne fixed-version package download karke extract bhi
kar liya (`Downloads\Microsoft.WebView2.FixedVersionRuntime.157.0.4341.46.x64\`
ke andar `msedgewebview2.exe` mojood — screenshot se sabit), lekin
DarkBox phir bhi "Download it now?" pooch raha tha. Wajah: v4.1.4 ka
setup sirf `.cab` file dhoondhta tha, jo extract hone ke baad khatam
ho chuki thi; aur DarkBox Downloads mein nahi dhoondhta — runtime use
apne saath chahiye.

**Fix (v4.1.5, setup-boot):**

1. `FindExtractedFixedVersionFolder()` — Downloads, Desktop ya setup
   wali folder mein `Microsoft.WebView2.FixedVersionRuntime.*` folder
   dhoondhta hai, aur `FindFixedRuntimeExe` se tasdeeq karta hai ke
   andar (aik level nested bhi) `msedgewebview2.exe` mojood hai.
2. `TryUseExtractedFixedVersion()` — mile to use progress window
   ("Copying WebView2 Runtime…") ke saath app wali folder
   (`%LOCALAPPDATA%\Programs\HamiiFolderLocker\WebView2Runtime`) mein
   copy kar deta hai. Uske baad app (v4.1.4 se) use seedha use karti
   hai — na download, na install, na registration.
3. Tartib: pehle installed check, phir `.cab` package, phir extracted
   folder, phir Evergreen download — jo mile, wohi use hota hai.

**User ko kya karna hai:** bas v4.1.5 setup chalana hai — Downloads
wali extracted folder khud mil jaye gi aur copy ho jaye gi.

## Build (verified on this VM, .NET SDK 8.0.428 at ~/.dotnet)
```bat
dotnet build -c Release
copy bin\Release\net48\HamiiFolderLocker.exe pkg\HamiiFolderLocker\
mkdir build-payload && copy pkg\HamiiFolderLocker\* build-payload\
(cd build-payload && zip ../setup-boot/payload.zip *)
dotnet build setup-boot/DarkBoxSetup.csproj -c Release
```
